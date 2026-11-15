using Amazon.DynamoDBv2;
using TicketFlow.Orders.Core.Application;
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

    /// <summary>D-0012: step-failed con pago ya autorizado también reclama la compensación.</summary>
    [SkippableFact]
    public async Task Failure_note_also_wins_from_PAYMENT_APPROVED()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var purchase = NewPurchase();
        await repo.InitializeAsync(CancellationToken.None);
        await repo.CreateAsync(purchase, CancellationToken.None);
        var ct = CancellationToken.None;
        await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct);
        await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.SEATS_RESERVED, PurchaseStatus.PAYMENT_APPROVED, ct: ct);

        Assert.True(await repo.TryNoteFailureAsync(purchase.PurchaseId, "issue-tickets falló", ct));
        Assert.False(await repo.TryNoteFailureAsync(purchase.PurchaseId, "duplicado", ct));

        Assert.True(await repo.TryTransitionAsync(purchase.PurchaseId,
            PurchaseStatus.PAYMENT_APPROVED, PurchaseStatus.FAILED, reason: "issue-tickets falló", ct: ct));
    }

    // --- Holds (D-0017/D-0018) contra el emulador real ------------------------------

    private static async Task<Purchase> NewHoldAtAsync(DynamoDbPurchaseRepository repo, long expiresAt,
        CancellationToken ct)
    {
        var purchase = Purchase.Create(PurchaseIds.New(), "it-evt", 2, "4111111111111111",
            "it-key-" + Guid.NewGuid().ToString("N")[..8], DateTimeOffset.UtcNow, hold: true);
        await repo.InitializeAsync(ct);
        await repo.CreateAsync(purchase, ct);
        Assert.True(await repo.TryHoldReserveAsync(purchase.PurchaseId, expiresAt, 240m, ct));
        return purchase;
    }

    [SkippableFact]
    public async Task Hold_reserve_roundtrips_expiry_and_amount()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds();
        var purchase = await NewHoldAtAsync(repo, expiresAt, ct);

        var stored = await repo.GetAsync(purchase.PurchaseId, ct);
        Assert.True(stored!.Hold);
        Assert.Equal(expiresAt, stored.ExpiresAt); // expiración observable (AC1)
        Assert.Equal(240m, stored.Amount); // monto persistido para el pago diferido
    }

    [SkippableFact]
    public async Task Scan_finds_only_due_holds_not_fresh_claimed_or_non_hold()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var due = await NewHoldAtAsync(repo, now - 10, ct); // vencido sin claim ⇒ candidato
        var fresh = await NewHoldAtAsync(repo, now + 120, ct); // vigente ⇒ fuera
        var claimed = await NewHoldAtAsync(repo, now + 30, ct); // vigente AL crearla: el claim gana...
        Assert.Equal(PaymentClaimOutcome.Claimed, await repo.TryClaimPaymentAsync(claimed.PurchaseId, ct));
        // ...y al avanzar el reloj (scan "ahora+60") sigue protegida por el claim (D-0017 §4).
        var nonHold = NewPurchase(); // SEATS_RESERVED sin hold ⇒ invisible para el sweeper
        await repo.CreateAsync(nonHold, ct);
        await repo.TryTransitionAsync(nonHold.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct);

        // Tabla compartida con el resto de la suite: el scan puede traer holds vencidos de
        // otros tests (correcto — el sweeper los quiere); LO verificable acá son los MÍOS.
        var mine = new HashSet<string> { due.PurchaseId, fresh.PurchaseId, claimed.PurchaseId, nonHold.PurchaseId };
        var found = (await repo.ScanExpirableHoldsAsync(now + 60, now + 60 - 400, ct))
            .Where(p => mine.Contains(p.PurchaseId)).ToList();

        Assert.Equal([due.PurchaseId], found.Select(p => p.PurchaseId).OrderBy(x => x).ToList());
        Assert.Equal(2, Assert.Single(found, p => p.PurchaseId == due.PurchaseId).Seats); // payload para release-seats
    }

    [SkippableFact]
    public async Task Claim_cas_wins_once_then_blocks_sweeper()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var purchase = await NewHoldAtAsync(repo,
            DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds(), ct);

        Assert.Equal(PaymentClaimOutcome.Claimed, await repo.TryClaimPaymentAsync(purchase.PurchaseId, ct));
        Assert.Equal(PaymentClaimOutcome.AlreadyClaimed, // replay del endpoint: 200, sin efectos
            await repo.TryClaimPaymentAsync(purchase.PurchaseId, ct));
        Assert.False(await repo.TryExpireAsync(purchase.PurchaseId, "no debería ganar", ct)); // D-0017 §4

        var stored = await repo.GetAsync(purchase.PurchaseId, ct);
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, stored!.Status); // la saga sigue su curso
        Assert.NotNull(stored.PaymentInitiatedAt);
    }

    [SkippableFact]
    public async Task Claim_after_expiry_is_expired_and_never_mutates()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var purchase = await NewHoldAtAsync(repo,
            DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(), ct);

        Assert.Equal(PaymentClaimOutcome.Expired, // vencida: ni siquiera el claim entra (AC4)
            await repo.TryClaimPaymentAsync(purchase.PurchaseId, ct));
        Assert.True(await repo.TryExpireAsync(purchase.PurchaseId, "reserva expirada sin pago", ct));
        Assert.False(await repo.TryExpireAsync(purchase.PurchaseId, "duplicado", ct)); // terminal de verdad

        var stored = await repo.GetAsync(purchase.PurchaseId, ct);
        Assert.Equal(PurchaseStatus.EXPIRED, stored!.Status);
        Assert.Equal("reserva expirada sin pago", stored.FailureReason); // terminal con motivo
    }

    [SkippableFact]
    public async Task Claim_on_missing_non_hold_or_advanced_returns_notfound_notclaimable()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        await repo.InitializeAsync(ct);

        Assert.Equal(PaymentClaimOutcome.NotFound, await repo.TryClaimPaymentAsync(PurchaseIds.New(), ct));

        var nonHold = NewPurchase(); // SEATS_RESERVED sin hold (happy path V1): 409 invalid-state
        await repo.CreateAsync(nonHold, ct);
        await repo.TryTransitionAsync(nonHold.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct);
        Assert.Equal(PaymentClaimOutcome.NotClaimable, await repo.TryClaimPaymentAsync(nonHold.PurchaseId, ct));

        var advanced = await NewHoldAtAsync(repo,
            DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds(), ct);
        await repo.TryTransitionAsync(advanced.PurchaseId, // el pago ya voló: estado avanzado
            PurchaseStatus.SEATS_RESERVED, PurchaseStatus.PAYMENT_APPROVED, ct: ct);
        Assert.Equal(PaymentClaimOutcome.NotClaimable, await repo.TryClaimPaymentAsync(advanced.PurchaseId, ct));
    }

    [SkippableFact]
    public async Task Zombie_claim_is_reclaimed_only_past_grace_cutoff()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var purchase = await NewHoldAtAsync(repo,
            DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeSeconds(), ct);
        Assert.Equal(PaymentClaimOutcome.Claimed, await repo.TryClaimPaymentAsync(purchase.PurchaseId, ct));
        var claimedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Dentro de la gracia (2×TTL+30 con TTL de prueba amplio): ni scan ni CAS lo tocan.
        // (Tabla compartida: solo las filas de ESTA compra — otras vencidas ajenas no importan.)
        var inGrace = (await repo.ScanExpirableHoldsAsync(claimedAt + 100, claimedAt + 100 - 400, ct))
            .Where(p => p.PurchaseId == purchase.PurchaseId).ToList();
        Assert.Empty(inGrace);
        Assert.False(await repo.TryExpireZombieAsync(purchase.PurchaseId, "zombi", claimedAt + 100 - 400, ct));

        // Pasada la gracia: tier de contingencia (D-0017 §5) — terminal con motivo.
        var lateCutoff = claimedAt + 500;
        Assert.Contains(purchase.PurchaseId,
            (await repo.ScanExpirableHoldsAsync(claimedAt + 500, lateCutoff, ct)).Select(p => p.PurchaseId));
        Assert.True(await repo.TryExpireZombieAsync(purchase.PurchaseId, "contingencia", lateCutoff, ct));
        Assert.Equal(PurchaseStatus.EXPIRED, (await repo.GetAsync(purchase.PurchaseId, ct))!.Status);
    }
}
