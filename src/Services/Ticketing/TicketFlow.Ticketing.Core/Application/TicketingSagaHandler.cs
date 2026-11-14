using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Ticketing.Core.Application;

/// <summary>
/// Handler de issue-tickets (D-0004): emite una entrada por asiento con su payload QR en S3
/// (D-0005), claim idempotente por compra (un duplicado NO emite entradas nuevas — hook H-03 AC2)
/// y responde tickets-issued. La "notificación" de emisión es un evento interno + log (fallback
/// D-0006/L-002: el fan-out de SNS no entrega en el emulador).
/// </summary>
public sealed class TicketingSagaHandler(
    ITicketRepository tickets,
    IQrPayloadStore qrStore,
    ILogger<TicketingSagaHandler> logger)
{
    public async Task<SagaEnvelope?> HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Type != SagaTypes.IssueTickets)
        {
            logger.LogWarning("comando de saga desconocido '{Type}' en Ticketing: ignorado", envelope.Type);
            return null;
        }

        var command = envelope.PayloadAs<IssueTicketsCommand>();
        if (string.IsNullOrWhiteSpace(command.EventId) || command.Seats is < 1 or > 8)
        {
            logger.LogError("issue-tickets de compra {PurchaseId} malformado: descartado", envelope.PurchaseId);
            return null;
        }

        // Genera primero, escribe solo si gana el claim: un duplicado no crea entradas nuevas.
        var ids = Enumerable.Range(0, command.Seats)
            .Select(_ => TicketIds.New())
            .ToList();
        var previousIds = await tickets.ClaimIssuanceAsync(envelope.PurchaseId, ids, ct);
        if (previousIds is not null)
        {
            logger.LogInformation("issue-tickets duplicado de compra {PurchaseId}: {Count} entradas YA emitidas, " +
                                  "no se emiten nuevas (duplicado descartado)", envelope.PurchaseId, previousIds.Count);
            ids = previousIds.ToList();
        }
        else
        {
            foreach (var (index, ticketId) in ids.Index())
            {
                var seat = index + 1;
                var qrKey = $"qr/{envelope.PurchaseId}/{ticketId}.json";
                await qrStore.PutAsync(qrKey, JsonSerializer.Serialize(new
                {
                    ticket_id = ticketId,
                    purchase_id = envelope.PurchaseId,
                    event_id = command.EventId,
                    seat,
                    issued_at = DateTimeOffset.UtcNow,
                }), ct);
                await tickets.SaveAsync(new Ticket(ticketId, envelope.PurchaseId, command.EventId, seat, qrKey), ct);
            }

            // Notificación = evento interno + log visible (fallback D-0006/L-002; SNS en V1).
            logger.LogInformation(
                "NOTIFICACIÓN (evento interno): {Count} entradas emitidas para compra {PurchaseId} del evento " +
                "{EventId} — ids {Ids}", ids.Count, envelope.PurchaseId, command.EventId, ids);
        }

        return new SagaEnvelope(SagaTypes.TicketsIssued, envelope.PurchaseId,
            JsonSerializer.SerializeToElement(new TicketsIssuedEvent(ids), SagaJson.Options));
    }
}
