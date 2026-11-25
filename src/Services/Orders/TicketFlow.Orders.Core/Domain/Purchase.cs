using System.Text.RegularExpressions;

namespace TicketFlow.Orders.Core.Domain;

/// <summary>
/// Estados de la saga expuestos por GET /purchases/{id} (H-02 AC4). Los nombres del enum SON el
/// formato wire/persistencia — no hay capa de traducción.
/// </summary>
public enum PurchaseStatus
{
    PENDING,
    SEATS_RESERVED,
    PAYMENT_APPROVED,
    TICKETS_ISSUED,
    FAILED,

    /// <summary>Terminal del hold (D-0017/D-0018): la reserva expiró sin pago y el asiento
    /// volvió al pool. NO reemplaza a FAILED — es un terminal más de "toda compra termina
    /// con motivo" (F-V1-01).</summary>
    EXPIRED,
}

/// <summary>Id de compra: "pur-" + 32 hex — seguro para URL y para clave de DynamoDB.</summary>
public static partial class PurchaseIds
{
    [GeneratedRegex(@"^pur-[0-9a-f]{32}$")]
    private static partial Regex Pattern();

    public static string New() => "pur-" + Guid.NewGuid().ToString("N");

    public static bool IsValid(string id) => Pattern().IsMatch(id);
}

/// <summary>
/// Agregado de compra: lo persisted por Órdenes y lo único que el process manager muta (D-0004).
/// La PAN se guarda para emitir authorize-payment cuando llega seats-reserved; el CVV nunca se
/// persiste. Idempotency-Key se guarda desde S02 como hook del idempotency store de H-03 (D-0008).
/// Campos del hold (D-0017/D-0018): Hold marca la compra cuyo pago NO se auto-emite; ExpiresAt
/// (epoch segundos) la vencimiento; Amount el monto persistido en la reserva para el pago
/// diferido; PaymentInitiatedAt (epoch segundos) el claim del POST /payment.
/// Owner (D-0024): "sub" del usuario de Cognito dueño de la compra.
/// </summary>
public sealed record Purchase(
    string PurchaseId,
    string EventId,
    int Seats,
    string Pan,
    PurchaseStatus Status,
    DateTimeOffset CreatedAt,
    string IdempotencyKey = "",
    string? FailureReason = null,
    string? PendingFailureReason = null,
    IReadOnlyList<string>? TicketIds = null,
    bool Hold = false,
    long? ExpiresAt = null,
    long? PaymentInitiatedAt = null,
    decimal? Amount = null,
    string Owner = "")
{
    public static Purchase Create(string purchaseId, string eventId, int seats, string pan,
        string idempotencyKey, DateTimeOffset createdAt, bool hold = false, string owner = "")
    {
        if (!PurchaseIds.IsValid(purchaseId))
            throw new ArgumentException($"Id de compra inválido: '{purchaseId}'.", nameof(purchaseId));
        if (seats is < 1 or > 8)
            throw new ArgumentException("Los asientos deben estar entre 1 y 8.", nameof(seats));
        if (string.IsNullOrWhiteSpace(pan))
            throw new ArgumentException("La PAN es obligatoria.", nameof(pan));
        return new Purchase(purchaseId, eventId, seats, pan, PurchaseStatus.PENDING, createdAt,
            idempotencyKey) { Hold = hold, Owner = owner };
    }
}

/// <summary>
/// TTL de los holds (D-0017 §6): HOLD_TTL_SECONDS por env, default 90, rango 5..3600 validado
/// fail-fast al arranque de Órdenes (NFR-1). Solo Órdenes lo lee — es el dueño del ciclo de vida.
/// </summary>
public static class HoldTtl
{
    public const int DefaultSeconds = 90;
    public const int MinSeconds = 5;
    public const int MaxSeconds = 3600;

    /// <summary>Precedencia (D-0024): env HOLD_TTL_SECONDS &gt; Parameter Store &gt; default.</summary>
    public static int FromEnvironment(string? fromParameterStore = null)
    {
        var raw = Environment.GetEnvironmentVariable("HOLD_TTL_SECONDS");
        if (string.IsNullOrWhiteSpace(raw)) raw = fromParameterStore;
        if (string.IsNullOrWhiteSpace(raw)) return DefaultSeconds;
        if (!int.TryParse(raw, out var value) || value is < MinSeconds or > MaxSeconds)
            throw new InvalidOperationException(
                $"HOLD_TTL_SECONDS inválido: '{raw}' — debe ser un entero entre {MinSeconds} y " +
                $"{MaxSeconds} (default {DefaultSeconds}).");
        return value;
    }
}
