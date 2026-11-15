using Microsoft.Extensions.Logging;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Core.Application;

/// <summary>
/// Process manager de la saga (D-0004): único dueño de las transiciones de compra. Cada evento
/// entrante aplica una transición CAS; SOLO si la transición gana se emite el comando siguiente —
/// un evento duplicado no vuelve a emitir (hook de H-03 AC2). Flujo:
/// seats-reserved→authorize-payment · payment-approved→issue-tickets · tickets-issued→TICKETS_ISSUED ·
/// payment-declined→release-seats (compensación) · seats-released→FAILED · seats-unavailable→FAILED ·
/// step-failed (D-0012, reintentos agotados)→FAILED directo (PENDING) o compensación (con reserva) ·
/// step-failed de release-seats (F-V1-01)→FAILED con la reserva huérfana declarada.
/// Holds (D-0017/D-0018): seats-reserved de hold→SEATS_RESERVED en espera (sin authorize-payment,
/// con expires_at); los eventos tardíos sobre EXPIRED pierden el CAS y se ignoran (§5) — el
/// agregado terminal no muta y el asiento ya volvió al pool por la expiración.
/// </summary>
public sealed class SagaProcessManager(
    IPurchaseRepository purchases,
    IOutbox outbox,
    ILogger<SagaProcessManager> logger,
    int holdTtlSeconds = HoldTtl.DefaultSeconds)
{
    public async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case SagaTypes.SeatsReserved:
            {
                var evt = envelope.PayloadAs<SeatsReservedEvent>();
                // D-0017 §1/D-0018 §1: en holds la transición a SEATS_RESERVED escribe expires_at
                // (+ monto del pago diferido) en el MISMO CAS — crash-safe: nunca existe un hold
                // sin expiración. Sin hold el camino es exactamente el de V1.
                var before = await purchases.GetAsync(envelope.PurchaseId, ct);
                var won = before?.Hold == true
                    ? await purchases.TryHoldReserveAsync(envelope.PurchaseId,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + holdTtlSeconds,
                        evt.UnitPrice * before.Seats, ct)
                    : await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct);
                if (!won)
                {
                    Ignored(envelope, $"compra no está PENDING (duplicado de {SagaTypes.SeatsReserved}?)");
                    return;
                }
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                if (purchase.Hold)
                {
                    // D-0018 §1: el hold NO emite authorize-payment — espera el claim del
                    // POST /purchases/{id}/payment dentro del TTL.
                    logger.LogInformation(
                        "compra {PurchaseId}: paso 'seats-reserved' → SEATS_RESERVED en hold " +
                        "(expira {ExpiresAt:O}) — pago diferido a POST /purchases/{PurchaseId}/payment",
                        envelope.PurchaseId,
                        purchase.ExpiresAt is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp) : DateTimeOffset.MaxValue,
                        envelope.PurchaseId);
                    break;
                }
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
                // Desde SEATS_RESERVED (payment-declined/step-failed) o PAYMENT_APPROVED
                // (step-failed con pago ya autorizado — D-0012).
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                var reason = purchase.PendingFailureReason ?? "pago rechazado y asientos liberados";
                if (await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.SEATS_RESERVED, PurchaseStatus.FAILED, reason: reason, ct: ct)
                    || await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.PAYMENT_APPROVED, PurchaseStatus.FAILED, reason: reason, ct: ct))
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

            case SagaTypes.StepFailed:
            {
                // Un worker de comando agotó sus 3 intentos (D-0012): la compra SIEMPRE queda
                // terminal con motivo. PENDING (falló antes de reservar) → FAILED directo;
                // con reserva hecha (SEATS_RESERVED/PAYMENT_APPROVED) → compensación reutilizada;
                // fallo de la propia compensación → FAILED con la reserva huérfana declarada.
                var evt = envelope.PayloadAs<StepFailedEvent>();
                if (await purchases.TryTransitionAsync(envelope.PurchaseId,
                        PurchaseStatus.PENDING, PurchaseStatus.FAILED, reason: evt.Reason, ct: ct))
                {
                    logger.LogInformation(
                        "compra {PurchaseId}: paso '{Step}' falló sin reserva previa → FAILED ({Reason})",
                        envelope.PurchaseId, evt.Step, evt.Reason);
                    break;
                }

                // F-V1-01 (enmienda D-0012): la PROPIA compensación agotó sus reintentos — la
                // compra ya no puede liberar el asiento sola. Terminal FAILED explícito que
                // declara la reserva huérfana (nunca silencio): la disponibilidad refleja el
                // asiento tomado y el motivo lo nombra; la entrega queda en la DLQ del Catálogo
                // para una liberación por reintento futuro. NO se re-emite release-seats.
                if (evt.Step == SagaTypes.ReleaseSeats)
                {
                    var orphan = await purchases.GetAsync(envelope.PurchaseId, ct);
                    var reason =
                        $"{orphan?.PendingFailureReason ?? evt.Reason}; además la compensación " +
                        "'release-seats' falló tras agotar sus reintentos — asiento reservado " +
                        "huérfano (entrega en la DLQ del Catálogo)";
                    if (await purchases.TryTransitionAsync(envelope.PurchaseId,
                            PurchaseStatus.SEATS_RESERVED, PurchaseStatus.FAILED, reason: reason, ct: ct)
                        || await purchases.TryTransitionAsync(envelope.PurchaseId,
                            PurchaseStatus.PAYMENT_APPROVED, PurchaseStatus.FAILED, reason: reason, ct: ct))
                    {
                        logger.LogError(
                            "compra {PurchaseId}: la compensación 'release-seats' también falló → " +
                            "FAILED con asiento huérfano declarado (ver DLQ del Catálogo)",
                            envelope.PurchaseId);
                        break;
                    }
                    Ignored(envelope, "fallo de compensación de compra ya terminal (duplicado de step-failed?)");
                    break;
                }

                if (!await purchases.TryNoteFailureAsync(envelope.PurchaseId, evt.Reason, ct))
                {
                    Ignored(envelope, "fallo ya anotado o compra no compensable (duplicado de step-failed?)");
                    break;
                }
                var purchase = await purchases.GetAsync(envelope.PurchaseId, ct);
                if (purchase is null) return;
                await outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReleaseSeats,
                    envelope.PurchaseId, new ReleaseSeatsCommand(purchase.EventId, purchase.Seats), ct);
                logger.LogInformation(
                    "compra {PurchaseId}: paso '{Step}' falló con reserva hecha ({Reason}) → compensación: emito release-seats",
                    envelope.PurchaseId, evt.Step, evt.Reason);
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
