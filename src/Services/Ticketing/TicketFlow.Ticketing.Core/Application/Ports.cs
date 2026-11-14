namespace TicketFlow.Ticketing.Core.Application;

/// <summary>Id de ticket: "tkt-" + 32 hex — seguro para URL y para clave de DynamoDB.</summary>
public static partial class TicketIds
{
    public static string New() => "tkt-" + Guid.NewGuid().ToString("N");
}

/// <summary>
/// Entrada emitida (H-02 AC1): id referenciable por GET /tickets/{id}, con el payload QR en S3.
/// </summary>
public sealed record Ticket(string Id, string PurchaseId, string EventId, int Seat, string QrS3Key);

/// <summary>Puerto de persistencia de entradas (patrón Repository, D-0008).</summary>
public interface ITicketRepository
{
    Task InitializeAsync(CancellationToken ct);

    Task SaveAsync(Ticket ticket, CancellationToken ct);

    Task<Ticket?> GetAsync(string ticketId, CancellationToken ct);

    /// <summary>
    /// Claim idempotente de la emisión por compra (ISSUANCE#purchase_id con los ids emitidos):
    /// gana una sola vez — un issue-tickets duplicado NO emite entradas nuevas (hook H-03 AC2),
    /// devuelve los ids ya emitidos para re-publicar el ack.
    /// </summary>
    Task<IReadOnlyList<string>?> ClaimIssuanceAsync(string purchaseId, IReadOnlyList<string> ticketIds,
        CancellationToken ct);

    Task<IReadOnlyList<string>?> GetIssuedTicketIdsAsync(string purchaseId, CancellationToken ct);
}

/// <summary>Puerto del artefacto QR (S3 del emulador, D-0005) — testeable con un fake en memoria.</summary>
public interface IQrPayloadStore
{
    Task InitializeAsync(CancellationToken ct);

    Task PutAsync(string key, string payloadJson, CancellationToken ct);

    Task<string?> GetAsync(string key, CancellationToken ct);
}
