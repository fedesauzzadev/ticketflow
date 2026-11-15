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

    /// <summary>
    /// Transición PENDING→SEATS_RESERVED de un HOLD (D-0017 §1): escribe expires_at y el monto
    /// persistido en el MISMO UpdateItem condicional — un crash no puede dejar un hold sin
    /// expiración observable. CAS por #st=PENDING AND hold=1.
    /// </summary>
    Task<bool> TryHoldReserveAsync(string purchaseId, long expiresAtEpochSeconds, decimal amount,
        CancellationToken ct);

    /// <summary>
    /// Sweeper (D-0017 §2): holds SEATS_RESERVED vencidos (sin claim) o con claim zombi
    /// (payment_initiated_at &lt; cutoff de contingencia 2×TTL+30 s, §5). El filtro excluye por
    /// construcción a las compras sin hold (nunca llevan expires_at).
    /// </summary>
    Task<IReadOnlyList<Purchase>> ScanExpirableHoldsAsync(long nowEpochSeconds,
        long zombieCutoffEpochSeconds, CancellationToken ct);

    /// <summary>
    /// CAS SEATS_RESERVED→EXPIRED (D-0017 §2/§4): gana SOLO si ningún pago reclamó la compra
    /// (attribute_not_exists(payment_initiated_at)) — la carrera pago-vs-vencimiento se resuelve
    /// en la capa de datos. Terminal con reason (F-V1-01).
    /// </summary>
    Task<bool> TryExpireAsync(string purchaseId, string reason, CancellationToken ct);

    /// <summary>Tier de contingencia (D-0017 §5): reclama el hold cuyo claim quedó zombi
    /// (payment_initiated_at &lt; cutoff) — cierra el único hueco de terminalidad del hold.</summary>
    Task<bool> TryExpireZombieAsync(string purchaseId, string reason, long cutoffEpochSeconds,
        CancellationToken ct);

    /// <summary>
    /// Claim del pago (D-0018 §3, D-0017 §4): CAS que escribe payment_initiated_at solo si la
    /// compra es hold SEATS_RESERVED vigente (expires_at &gt; now) sin claim previo. Determina
    /// quién gana la carrera contra el sweeper.
    /// </summary>
    Task<PaymentClaimOutcome> TryClaimPaymentAsync(string purchaseId, CancellationToken ct);
}

/// <summary>Resultado del claim de pago (D-0018 §3) — mapea 1:1 al contrato HTTP.</summary>
public enum PaymentClaimOutcome
{
    /// <summary>202: claim ganado — el caller emite authorize-payment.</summary>
    Claimed,

    /// <summary>200: replay del endpoint (claim ya tomado, sin efectos).</summary>
    AlreadyClaimed,

    /// <summary>409 hold-expired: la reserva ya venció y liberó.</summary>
    Expired,

    /// <summary>409 invalid-state: no es hold, o su estado ya avanzó.</summary>
    NotClaimable,

    /// <summary>404 purchase-not-found.</summary>
    NotFound,
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
