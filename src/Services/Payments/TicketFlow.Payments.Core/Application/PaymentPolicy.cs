namespace TicketFlow.Payments.Core.Application;

/// <summary>
/// Simulador determinístico de autorización (convenio del plan §2): PAN terminada en "0002" ⇒
/// rechazada; cualquier otra ⇒ aprobada. Regla pura — unit testeada sin red.
/// </summary>
public static class PaymentPolicy
{
    public const string DecliningSuffix = "0002";

    public static (bool Approved, string? Reason) Decide(string pan) =>
        pan.EndsWith(DecliningSuffix, StringComparison.Ordinal)
            ? (false, $"pago rechazado por el emisor (PAN terminada en {DecliningSuffix})")
            : (true, null);
}

/// <summary>Autorización persistida: evidencia del paso de pago (GET /payments/{purchase_id}).</summary>
public sealed record Payment(
    string PurchaseId,
    bool Approved,
    string? Reason,
    decimal Amount,
    string PanLast4,
    DateTimeOffset DecidedAt);

/// <summary>
/// Puerto de pagos: idempotente por AUTH#purchase_id — SaveIfNewAsync gana una sola vez
/// (put condicional); un authorize-payment duplicado re-lee la decisión ya tomada.
/// </summary>
public interface IPaymentRepository
{
    Task InitializeAsync(CancellationToken ct);

    Task<bool> SaveIfNewAsync(Payment payment, CancellationToken ct);

    Task<Payment?> GetAsync(string purchaseId, CancellationToken ct);
}
