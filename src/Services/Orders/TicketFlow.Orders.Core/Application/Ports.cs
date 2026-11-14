using TicketFlow.Orders.Core.Domain;

namespace TicketFlow.Orders.Core.Application;

/// <summary>
/// Puerto de persistencia del agregado compra (patrón Repository, D-0008). Las transiciones son
/// CAS (compare-and-set): devuelven false si el estado ya cambió — ese guard es el dedup natural
/// de eventos duplicados (hook de H-03: sin efecto nuevo, sin re-emitir comandos).
/// </summary>
public interface IPurchaseRepository
{
    Task InitializeAsync(CancellationToken ct);

    Task CreateAsync(Purchase purchase, CancellationToken ct);

    Task<Purchase?> GetAsync(string purchaseId, CancellationToken ct);

    Task<bool> TryTransitionAsync(string purchaseId, PurchaseStatus from, PurchaseStatus to,
        string? reason = null, IReadOnlyList<string>? ticketIds = null, CancellationToken ct = default);

    /// <summary>
    /// Anota el motivo de rechazo del pago una sola vez (estado SEATS_RESERVED, sin failure_reason
    /// previo) y devuelve true solo la primera vez — quien gana emite release-seats exactamente una vez.
    /// </summary>
    Task<bool> TryNoteFailureAsync(string purchaseId, string reason, CancellationToken ct);
}

/// <summary>
/// Outbox de Órdenes (D-0008 patrón 3): persistir el comando a emitir y que un relay lo publique a
/// SQS. At-least-once: el dedup id de SQS + los claims del receptor toleran reenvíos.
/// </summary>
public interface IOutbox
{
    Task EnqueueAsync(string queueName, string type, string purchaseId, object payload, CancellationToken ct);
}

/// <summary>
/// Peek de disponibilidad del Catálogo para la validación sincrónica del POST (H-03 AC3,
/// D-0009). null = "no se pudo validar" (Catálogo caído, 404, timeout): el caller hace
/// fail-open — el guard definitivo es el decremento condicional de la saga.
/// </summary>
public interface ICatalogAvailabilityGateway
{
    Task<int?> GetAvailabilityAsync(string eventId, CancellationToken ct);
}
