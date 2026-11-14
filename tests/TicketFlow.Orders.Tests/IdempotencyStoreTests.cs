using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Idempotency-Key store (H-03 AC1, D-0009) contra DynamoDB real del emulador: claim
/// condicional por key — la primera request gana, las demás reciben el registro existente.
/// </summary>
public sealed class IdempotencyStoreTests
{
    private static DynamoDbIdempotencyStore NewStore() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    private static string NewKey() => "it-key-" + Guid.NewGuid().ToString("N");

    [SkippableFact]
    public async Task Find_unknown_key_returns_null()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var store = NewStore();
        await store.InitializeAsync(CancellationToken.None);

        Assert.Null(await store.FindAsync(NewKey(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Claim_is_won_once_and_every_retry_reads_the_same_registration()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var store = NewStore();
        await store.InitializeAsync(ct);
        var key = NewKey();
        var purchaseId = PurchaseIds.New();

        // Nadie la tenía: esta request crea la compra.
        Assert.True(await store.TryClaimAsync(key, "hash-1", purchaseId, ct));

        // Replay mismo body y reintento con body distinto: NINGUNO gana el claim —
        // ambos leen el registro original (replay / mismatch 422 en la API).
        Assert.False(await store.TryClaimAsync(key, "hash-1", PurchaseIds.New(), ct));
        Assert.False(await store.TryClaimAsync(key, "hash-otro", PurchaseIds.New(), ct));

        var claim = await store.FindAsync(key, ct);
        Assert.NotNull(claim);
        Assert.Equal(purchaseId, claim!.PurchaseId);
        Assert.Equal("hash-1", claim.RequestHash);
    }
}
