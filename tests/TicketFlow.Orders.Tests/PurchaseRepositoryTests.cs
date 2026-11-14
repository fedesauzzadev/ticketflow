using Amazon.DynamoDBv2;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Integración del repositorio de compras contra DynamoDB real: roundtrip, transiciones CAS
/// (el guard de duplicados/desorden) y nota de fallo una sola vez.
/// </summary>
public sealed class PurchaseRepositoryTests
{
    private static DynamoDbPurchaseRepository NewRepo() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    private static Purchase NewPurchase() => Purchase.Create(
        PurchaseIds.New(), "it-evt", 1, "4111111111111111", "it-key", DateTimeOffset.UtcNow);

    [SkippableFact]
    public async Task Create_then_get_roundtrips()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var purchase = NewPurchase();
        await repo.InitializeAsync(CancellationToken.None);
        await repo.CreateAsync(purchase, CancellationToken.None);

        var stored = await repo.GetAsync(purchase.PurchaseId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(purchase, stored);
    }

    [SkippableFact]
    public async Task Get_missing_returns_null()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        await repo.InitializeAsync(CancellationToken.None);
        Assert.Null(await repo.GetAsync(PurchaseIds.New(), CancellationToken.None)); // L-004
    }

    [SkippableFact]
    public async Task Transition_succeeds_once_then_fails_on_duplicate_or_wrong_source()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var purchase = NewPurchase();
        await repo.InitializeAsync(CancellationToken.None);
        await repo.CreateAsync(purchase, CancellationToken.None);
        var ct = CancellationToken.None;

        Assert.True(await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct));
        Assert.False(await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct)); // duplicado: estado ya avanzó
        Assert.False(await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.TICKETS_ISSUED, ct: ct)); // salto ilegal

        Assert.True(await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.SEATS_RESERVED, PurchaseStatus.TICKETS_ISSUED,
            ticketIds: ["tkt-a", "tkt-b"], ct: ct));
        var final = await repo.GetAsync(purchase.PurchaseId, ct);
        Assert.Equal(PurchaseStatus.TICKETS_ISSUED, final!.Status);
        Assert.Equal(["tkt-a", "tkt-b"], final.TicketIds);
    }

    [SkippableFact]
    public async Task Failure_note_wins_once_and_carries_reason_to_failed_transition()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var purchase = NewPurchase();
        await repo.InitializeAsync(CancellationToken.None);
        await repo.CreateAsync(purchase, CancellationToken.None);
        var ct = CancellationToken.None;
        await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct);

        Assert.True(await repo.TryNoteFailureAsync(purchase.PurchaseId, "motivo original", ct));
        Assert.False(await repo.TryNoteFailureAsync(purchase.PurchaseId, "motivo duplicado", ct)); // una sola vez

        Assert.True(await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.SEATS_RESERVED, PurchaseStatus.FAILED, ct: ct));
        var failed = await repo.GetAsync(purchase.PurchaseId, ct);
        // El PM lee pending_failure_reason y lo pasa como reason en la transición a FAILED.
        Assert.Equal("motivo original", failed!.PendingFailureReason);
    }
}
