using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicketFlow.Catalog.Core.Domain;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Catalog.Core.Application;

/// <summary>
/// Handler de los comandos de saga del Catálogo (D-0004): reserve-seats / release-seats.
/// Aplica la mutación idempotente (DynamoDbEventRepository), invalida el cache de disponibilidad
/// (H-01 AC2 — la compra es el disparador real) y DEVUELVE los eventos a publicar a Órdenes;
/// el worker es quien los manda por SQS — así el handler se testea sin mensajería.
/// </summary>
public sealed class CatalogSagaHandler(
    ISeatReservationStore reservations,
    AvailabilityService availability,
    ILogger<CatalogSagaHandler> logger)
{
    public async Task<IReadOnlyList<SagaEnvelope>> HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        switch (envelope.Type)
        {
            case SagaTypes.ReserveSeats:
                return [.. await ReserveAsync(envelope, ct)];
            case SagaTypes.ReleaseSeats:
                return [.. await ReleaseAsync(envelope, ct)];
            default:
                logger.LogWarning("comando de saga desconocido '{Type}' en Catálogo: ignorado", envelope.Type);
                return [];
        }
    }

    private async Task<IEnumerable<SagaEnvelope>> ReserveAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        var command = envelope.PayloadAs<ReserveSeatsCommand>();
        if (!IsWellFormed(command))
        {
            logger.LogError("reserve-seats de compra {PurchaseId} malformado en la frontera SQS: descartado",
                envelope.PurchaseId);
            return [];
        }

        var result = await reservations.ReserveAsync(command.EventId, envelope.PurchaseId, command.Seats, ct);
        if (result.Outcome == ReservationOutcome.DuplicateReserved)
            logger.LogInformation(
                "comando 'reserve-seats' de compra {PurchaseId} duplicado detectado: efecto descartado (idempotencia por correlación)",
                envelope.PurchaseId);
        if (result.Outcome == ReservationOutcome.Unavailable)
            return [Envelope(envelope.PurchaseId, SagaTypes.SeatsUnavailable,
                new SeatsUnavailableEvent(command.EventId, "sin disponibilidad suficiente o evento inexistente"))];

        // H-01 AC2: la compra invalida el cache (también en el ack de un duplicado).
        await availability.InvalidateAsync(command.EventId, ct);
        return [Envelope(envelope.PurchaseId, SagaTypes.SeatsReserved,
            new SeatsReservedEvent(command.EventId, result.UnitPrice))];
    }

    private async Task<IEnumerable<SagaEnvelope>> ReleaseAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        var command = envelope.PayloadAs<ReleaseSeatsCommand>();
        if (!IsWellFormed(command))
        {
            logger.LogError("release-seats de compra {PurchaseId} malformado en la frontera SQS: descartado",
                envelope.PurchaseId);
            return [];
        }

        var outcome = await reservations.ReleaseAsync(command.EventId, envelope.PurchaseId, command.Seats, ct);
        if (outcome == ReleaseOutcome.Released)
            await availability.InvalidateAsync(command.EventId, ct);
        else
            logger.LogInformation(
                "comando 'release-seats' de compra {PurchaseId} duplicado detectado: efecto descartado (idempotencia por correlación)",
                envelope.PurchaseId);

        // El ack (también en duplicado) mantiene la compra avanzando hacia FAILED.
        return [Envelope(envelope.PurchaseId, SagaTypes.SeatsReleased, new SeatsReleasedEvent(command.EventId))];
    }

    /// <summary>Validación de la frontera SQS (NFR-1): comando malformado se descarta con log.</summary>
    private static bool IsWellFormed(ReserveSeatsCommand command) =>
        CatalogEventId.IsValid(command.EventId ?? "") && command.Seats is >= 1 and <= 8;

    private static bool IsWellFormed(ReleaseSeatsCommand command) =>
        CatalogEventId.IsValid(command.EventId ?? "") && command.Seats is >= 1 and <= 8;

    private static SagaEnvelope Envelope<T>(string purchaseId, string type, T payload) =>
        new(type, purchaseId, JsonSerializer.SerializeToElement(payload, SagaJson.Options));
}
