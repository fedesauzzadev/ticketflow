namespace TicketFlow.Catalog.Core.Application;

public sealed class EventNotFoundException(string eventId)
    : Exception($"El evento '{eventId}' no existe.");

/// <summary>
/// Puerto de persistencia del catálogo (patrón Repository, D-0008).
/// La impl de DynamoDB vive en Infrastructure; el dominio no conoce el almacén.
/// </summary>
public interface IEventRepository
{
    Task<IReadOnlyList<Domain.CatalogEvent>> ListAsync(CancellationToken ct);

    Task<Domain.CatalogEvent?> GetByIdAsync(string eventId, CancellationToken ct);
}
