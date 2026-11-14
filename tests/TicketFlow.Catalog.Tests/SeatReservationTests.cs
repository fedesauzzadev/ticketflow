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
}
