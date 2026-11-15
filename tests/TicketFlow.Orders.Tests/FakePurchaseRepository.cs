using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Fake de <see cref="IPurchaseRepository"/> con la MISMA semántica CAS que
/// DynamoDbPurchaseRepository (transiciones por estado, claim/expire excluyentes, scan filtrado):
/// hace testeable el process manager y el sweeper sin red, y es el oráculo del property test de
/// tormenta de eventos. Reloj inyectado para determinismo.
/// </summary>
public sealed class FakePurchaseRepository : IPurchaseRepository
{
    public Dictionary<string, Purchase> Store { get; } = [];
    public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    public Task CreateAsync(Purchase purchase, CancellationToken ct)
    {
        Store[purchase.PurchaseId] = purchase;
        return Task.CompletedTask;
    }

    public Task<Purchase?> GetAsync(string purchaseId, CancellationToken ct) =>
        Task.FromResult(Store.GetValueOrDefault(purchaseId));

    public Task<bool> TryTransitionAsync(string purchaseId, PurchaseStatus from, PurchaseStatus to,
        string? reason = null, IReadOnlyList<string>? ticketIds = null, CancellationToken ct = default)
    {
        if (!Store.TryGetValue(purchaseId, out var purchase) || purchase.Status != from)
            return Task.FromResult(false);
        Store[purchaseId] = purchase with
        {
            Status = to,
            FailureReason = reason ?? purchase.FailureReason,
            TicketIds = ticketIds ?? purchase.TicketIds,
        };
        return Task.FromResult(true);
    }

    public Task<bool> TryNoteFailureAsync(string purchaseId, string reason, CancellationToken ct)
    {
        if (!Store.TryGetValue(purchaseId, out var purchase)
            || purchase.Status is not (PurchaseStatus.SEATS_RESERVED or PurchaseStatus.PAYMENT_APPROVED)
            || purchase.PendingFailureReason is not null)
            return Task.FromResult(false);
        Store[purchaseId] = purchase with { PendingFailureReason = reason };
        return Task.FromResult(true);
    }

    public Task<bool> TryHoldReserveAsync(string purchaseId, long expiresAtEpochSeconds, decimal amount,
        CancellationToken ct)
    {
        if (!Store.TryGetValue(purchaseId, out var purchase)
            || purchase.Status != PurchaseStatus.PENDING || !purchase.Hold)
            return Task.FromResult(false);
        Store[purchaseId] = purchase with
        {
            Status = PurchaseStatus.SEATS_RESERVED,
            ExpiresAt = expiresAtEpochSeconds,
            Amount = amount,
        };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<Purchase>> ScanExpirableHoldsAsync(long nowEpochSeconds,
        long zombieCutoffEpochSeconds, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Purchase>>(Store.Values.Where(p =>
            p.Status == PurchaseStatus.SEATS_RESERVED &&
            ((p.ExpiresAt < nowEpochSeconds && p.PaymentInitiatedAt is null)
             || p.PaymentInitiatedAt < zombieCutoffEpochSeconds)).ToList());

    public Task<bool> TryExpireAsync(string purchaseId, string reason, CancellationToken ct)
    {
        if (!Store.TryGetValue(purchaseId, out var purchase)
            || purchase.Status != PurchaseStatus.SEATS_RESERVED
            || purchase.PaymentInitiatedAt is not null)
            return Task.FromResult(false);
        Store[purchaseId] = purchase with { Status = PurchaseStatus.EXPIRED, FailureReason = reason };
        return Task.FromResult(true);
    }

    public Task<bool> TryExpireZombieAsync(string purchaseId, string reason, long cutoffEpochSeconds,
        CancellationToken ct)
    {
        if (!Store.TryGetValue(purchaseId, out var purchase)
            || purchase.Status != PurchaseStatus.SEATS_RESERVED
            || purchase.PaymentInitiatedAt >= cutoffEpochSeconds)
            return Task.FromResult(false);
        Store[purchaseId] = purchase with { Status = PurchaseStatus.EXPIRED, FailureReason = reason };
        return Task.FromResult(true);
    }

    public Task<PaymentClaimOutcome> TryClaimPaymentAsync(string purchaseId, CancellationToken ct)
    {
        if (!Store.TryGetValue(purchaseId, out var purchase))
            return Task.FromResult(PaymentClaimOutcome.NotFound);
        var now = Now.ToUnixTimeSeconds();
        if (purchase.Status == PurchaseStatus.SEATS_RESERVED && purchase.Hold
            && purchase.ExpiresAt > now && purchase.PaymentInitiatedAt is null)
        {
            Store[purchaseId] = purchase with { PaymentInitiatedAt = now };
            return Task.FromResult(PaymentClaimOutcome.Claimed);
        }
        if (purchase.Status == PurchaseStatus.EXPIRED)
            return Task.FromResult(PaymentClaimOutcome.Expired);
        // Mismo orden de precedencia que el repo real: claim vigente > expiración de reloj (D-0017 §4).
        if (purchase.PaymentInitiatedAt is not null && purchase.Status == PurchaseStatus.SEATS_RESERVED)
            return Task.FromResult(PaymentClaimOutcome.AlreadyClaimed);
        if (purchase.Hold && purchase.Status == PurchaseStatus.SEATS_RESERVED
            && purchase.ExpiresAt <= now)
            return Task.FromResult(PaymentClaimOutcome.Expired);
        return Task.FromResult(PaymentClaimOutcome.NotClaimable);
    }
}
