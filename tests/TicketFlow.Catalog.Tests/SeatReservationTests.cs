using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;
using TicketFlow.Catalog.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Primitivas de reserva/liberación de la saga (S02, D-0005) contra DynamoDB real: decremento
/// condicional anti-oversell, idempotencia por claim, restauración exacta (compensación H-02 AC3).
/// </summary>
public sealed class SeatReservationTests
{
    private static DynamoDbEventRepository NewRepo() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    private static string NewId() => "it-" + Guid.NewGuid().ToString("N")[..12];

    private static CatalogEvent Event(string id, int available) =>
        CatalogEvent.Create(id, "Reservation Show",
            new DateTimeOffset(2027, 7, 1, 20, 0, 0, TimeSpan.Zero), 60m, available);

    [SkippableFact]
    public async Task Reserve_decrements_conditionally_and_duplicate_reserves_do_not_double_decrement()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var eventId = NewId();
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        await repo.InitializeAsync([Event(eventId, 5)], CancellationToken.None);
        var ct = CancellationToken.None;

        var first = await repo.ReserveAsync(eventId, purchaseId, 2, ct);
        Assert.Equal(ReservationOutcome.Reserved, first.Outcome);
        Assert.Equal(60m, first.UnitPrice); // el precio viaja al evento seats-reserved

        var duplicate = await repo.ReserveAsync(eventId, purchaseId, 2, ct);
        Assert.Equal(ReservationOutcome.DuplicateReserved, duplicate.Outcome);

        var stored = await repo.GetByIdAsync(eventId, ct);
        Assert.Equal(3, stored!.Available); // un solo decremento (−2), no −4
    }

    [SkippableFact]
    public async Task Reserve_more_than_available_is_rejected_without_mutation()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var eventId = NewId();
        await repo.InitializeAsync([Event(eventId, 1)], CancellationToken.None);
        var ct = CancellationToken.None;

        var result = await repo.ReserveAsync(eventId, "pur-" + Guid.NewGuid().ToString("N"), 2, ct);

        Assert.Equal(ReservationOutcome.Unavailable, result.Outcome);
        Assert.Equal(1, (await repo.GetByIdAsync(eventId, ct))!.Available); // nada cambió
    }

    [SkippableFact]
    public async Task Reserve_on_missing_event_is_unavailable()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        await repo.InitializeAsync([], CancellationToken.None);

        var result = await repo.ReserveAsync(NewId(), "pur-" + Guid.NewGuid().ToString("N"), 1, CancellationToken.None);

        Assert.Equal(ReservationOutcome.Unavailable, result.Outcome);
    }

    [SkippableFact]
    public async Task Release_restores_exactly_once_and_duplicate_release_does_not_over_increment()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var eventId = NewId();
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        await repo.InitializeAsync([Event(eventId, 5)], CancellationToken.None);
        var ct = CancellationToken.None;
        await repo.ReserveAsync(eventId, purchaseId, 2, ct); // 5 → 3

        var released = await repo.ReleaseAsync(eventId, purchaseId, 2, ct);
        var duplicate = await repo.ReleaseAsync(eventId, purchaseId, 2, ct);

        Assert.Equal(ReleaseOutcome.Released, released);
        Assert.Equal(ReleaseOutcome.Duplicate, duplicate);
        Assert.Equal(5, (await repo.GetByIdAsync(eventId, ct))!.Available); // restauración exacta (H-02 AC3)
    }

    // --- D-0013 (N-2002 AC5): decremento atómico idempotente single-item ---------------

    /// <summary>
    /// Simula el crash de la ventana vieja (F-05): se pierde el claim RESERVATION#pid tras el
    /// decremento — con el decremento single-item NO hay drift: el reprocesamiento reconoce el
    /// decremento ya hecho, NO decrementa de nuevo y reconstruye el claim para poder compensar.
    /// </summary>
    [SkippableFact]
    public async Task Crash_between_decrement_and_claim_no_drift_on_reprocess()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var eventId = NewId();
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        await repo.InitializeAsync([Event(eventId, 5)], CancellationToken.None);
        var ct = CancellationToken.None;

        var first = await repo.ReserveAsync(eventId, purchaseId, 2, ct); // 5 → 3, pid ∈ decrement_pids
        Assert.Equal(ReservationOutcome.Reserved, first.Outcome);

        // "Crash": la escritura del claim se pierde (borrado crudo del ítem RESERVATION#).
        using var ddb = EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment());
        await ddb.DeleteItemAsync(new Amazon.DynamoDBv2.Model.DeleteItemRequest
        {
            TableName = DynamoDbEventRepository.TableName,
            Key = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["pk"] = new() { S = "RESERVATION#" + purchaseId },
            },
        }, ct);

        // Reprocesamiento del comando (redelivery): ack SIN segundo decremento + claim rehecho.
        var reprocessed = await repo.ReserveAsync(eventId, purchaseId, 2, ct);
        Assert.Equal(ReservationOutcome.DuplicateReserved, reprocessed.Outcome);
        Assert.Equal(60m, reprocessed.UnitPrice); // el ack duplicado lleva el precio igual
        Assert.Equal(3, (await repo.GetByIdAsync(eventId, ct))!.Available); // exactamente UN decremento (AC5)

        // El claim reconstruido permite compensar, y la compensación restaura exacto.
        Assert.Equal(ReleaseOutcome.Released, await repo.ReleaseAsync(eventId, purchaseId, 2, ct));
        Assert.Equal(5, (await repo.GetByIdAsync(eventId, ct))!.Available);
    }

    /// <summary>
    /// AC5 invariante de disponibilidad con compras concurrentes sobre el mismo evento:
    /// N reservas paralelas de 1 asiento → exactamente N decrementos, sin oversell ni drift.
    /// </summary>
    [SkippableFact]
    public async Task Parallel_reserves_each_decrement_exactly_once()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var eventId = NewId();
        await repo.InitializeAsync([Event(eventId, 20)], CancellationToken.None);
        var ct = CancellationToken.None;

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            repo.ReserveAsync(eventId, "pur-" + Guid.NewGuid().ToString("N"), 1, ct)));

        Assert.Equal(8, results.Count(r => r.Outcome == ReservationOutcome.Reserved));
        Assert.Equal(12, (await repo.GetByIdAsync(eventId, ct))!.Available); // 20 − 8 exactos
    }

    /// <summary>El release borra el pid del set: la misma compra puede volver a reservar (sin fantasma de dedup).</summary>
    [SkippableFact]
    public async Task Release_removes_pid_so_reserve_again_decrements_again()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var eventId = NewId();
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        await repo.InitializeAsync([Event(eventId, 5)], CancellationToken.None);
        var ct = CancellationToken.None;

        await repo.ReserveAsync(eventId, purchaseId, 2, ct);   // 5 → 3
        await repo.ReleaseAsync(eventId, purchaseId, 2, ct);   // 3 → 5, pid fuera del set
        var again = await repo.ReserveAsync(eventId, purchaseId, 1, ct);

        Assert.Equal(ReservationOutcome.Reserved, again.Outcome); // NO es DuplicateReserved
        Assert.Equal(4, (await repo.GetByIdAsync(eventId, ct))!.Available);
    }
}
