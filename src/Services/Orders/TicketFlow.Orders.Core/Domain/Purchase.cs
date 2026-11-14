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
    IReadOnlyList<string>? TicketIds = null)
{
    public static Purchase Create(string purchaseId, string eventId, int seats, string pan,
        string idempotencyKey, DateTimeOffset createdAt)
    {
        if (!PurchaseIds.IsValid(purchaseId))
            throw new ArgumentException($"Id de compra inválido: '{purchaseId}'.", nameof(purchaseId));
        if (seats is < 1 or > 8)
            throw new ArgumentException("Los asientos deben estar entre 1 y 8.", nameof(seats));
        if (string.IsNullOrWhiteSpace(pan))
            throw new ArgumentException("La PAN es obligatoria.", nameof(pan));
        return new Purchase(purchaseId, eventId, seats, pan, PurchaseStatus.PENDING, createdAt,
            idempotencyKey);
    }
}
