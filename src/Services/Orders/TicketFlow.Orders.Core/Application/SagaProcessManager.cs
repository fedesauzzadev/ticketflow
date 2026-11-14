using Microsoft.Extensions.Logging;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Core.Application;

/// <summary>
/// Process manager de la saga (D-0004): único dueño de las transiciones de compra. Cada evento
/// entrante aplica una transición CAS; SOLO si la transición gana se emite el comando siguiente —
/// un evento duplicado no vuelve a emitir (hook de H-03 AC2). Flujo:
/// seats-reserved→authorize-payment · payment-approved→issue-tickets · tickets-issued→TICKETS_ISSUED ·
/// payment-declined→release-seats (compensación) · seats-released→FAILED · seats-unavailable→FAILED.
/// </summary>
public sealed class SagaProcessManager(
    IPurchaseRepository purchases,
    IOutbox outbox,
    ILogger<SagaProcessManager> logger)
{
    public async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case SagaTypes.SeatsReserved:
            {
                var evt = envelope.PayloadAs<SeatsReservedEvent>();
                if (!await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct))
                {
                    Ignored(envelope, $"compra no está PENDING (duplicado de {SagaTypes.SeatsReserved}?)");
                    return;
                }
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                // El monto nace del precio que dueña el Catálogo (viaja en el evento) — sin lectura cross-tabla.
                await outbox.EnqueueAsync(SagaQueues.PaymentsCommands, SagaTypes.AuthorizePayment,
                    envelope.PurchaseId,
                    new AuthorizePaymentCommand(purchase.Pan, evt.UnitPrice * purchase.Seats), ct);
                logger.LogInformation(
                    "compra {PurchaseId}: paso 'seats-reserved' → SEATS_RESERVED; emito authorize-payment",
                    envelope.PurchaseId);
                break;
            }

            case SagaTypes.PaymentApproved:
            {
                if (!await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.SEATS_RESERVED, PurchaseStatus.PAYMENT_APPROVED, ct: ct))
                {
                    Ignored(envelope, $"compra no está SEATS_RESERVED (duplicado de {SagaTypes.PaymentApproved}?)");
                    return;
                }
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                await outbox.EnqueueAsync(SagaQueues.TicketingCommands, SagaTypes.IssueTickets,
                    envelope.PurchaseId, new IssueTicketsCommand(purchase.EventId, purchase.Seats), ct);
                logger.LogInformation(
                    "compra {PurchaseId}: paso 'payment-approved' → PAYMENT_APPROVED; emito issue-tickets",
                    envelope.PurchaseId);
                break;
            }

            case SagaTypes.TicketsIssued:
            {
                var evt = envelope.PayloadAs<TicketsIssuedEvent>();
                if (await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.PAYMENT_APPROVED, PurchaseStatus.TICKETS_ISSUED,
                        ticketIds: evt.TicketIds, ct: ct))
                    logger.LogInformation(
                        "compra {PurchaseId}: paso 'tickets-issued' → TICKETS_ISSUED — saga completa",
                        envelope.PurchaseId);
                break;
            }

            case SagaTypes.PaymentDeclined:
            {
                var evt = envelope.PayloadAs<PaymentDeclinedEvent>();
                // La nota de fallo es el claim de la compensación: solo la primera emite release-seats.
                if (!await purchases.TryNoteFailureAsync(envelope.PurchaseId, evt.Reason, ct))
                {
                    Ignored(envelope, "fallo ya anotado (duplicado de payment-declined)");
                    return;
                }
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                await outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReleaseSeats,
                    envelope.PurchaseId, new ReleaseSeatsCommand(purchase.EventId, purchase.Seats), ct);
                logger.LogInformation(
                    "compra {PurchaseId}: paso 'payment-declined' ({Reason}) → compensación: emito release-seats",
                    envelope.PurchaseId, evt.Reason);
                break;
            }

            case SagaTypes.SeatsReleased:
            {
                // Compensación completada: ahora sí FAILED con el motivo visible (H-02 AC3).
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                if (await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.SEATS_RESERVED, PurchaseStatus.FAILED,
                        reason: purchase.PendingFailureReason ?? "pago rechazado y asientos liberados", ct: ct))
                    logger.LogInformation(
                        "compra {PurchaseId}: paso 'seats-released' → FAILED — compensación completada, asiento liberado",
                        envelope.PurchaseId);
                break;
            }

            case SagaTypes.SeatsUnavailable:
            {
                var evt = envelope.PayloadAs<SeatsUnavailableEvent>();
                if (await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.PENDING, PurchaseStatus.FAILED, reason: evt.Reason, ct: ct))
                    logger.LogInformation(
                        "compra {PurchaseId}: paso 'seats-unavailable' → FAILED ({Reason})",
                        envelope.PurchaseId, evt.Reason);
                break;
            }

            default:
                logger.LogWarning("evento de saga desconocido '{Type}' en Órdenes: ignorado", envelope.Type);
                break;
        }
    }

    private void Ignored(SagaEnvelope envelope, string why) =>
        logger.LogInformation("evento '{Type}' de compra {PurchaseId} ignorado: {Why} (duplicado descartado)",
            envelope.Type, envelope.PurchaseId, why);
}
