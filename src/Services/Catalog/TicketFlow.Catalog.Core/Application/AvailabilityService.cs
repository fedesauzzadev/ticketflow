using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using TicketFlow.Catalog.Core.Domain;

namespace TicketFlow.Catalog.Core.Application;

public sealed class AvailabilityCacheOptions
{
    /// <summary>TTL del cache de disponibilidad (D-0007: 30 s acota cualquier drift residual).</summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Disponibilidad leída + origen (observable: header X-Cache, H-01 AC1).</summary>
public sealed record CacheOutcome(int Available, bool FromCache);

/// <summary>
/// Cache-aside sobre la disponibilidad (D-0007): MISS → lee el repositorio y pobla el cache
/// con TTL; HIT → sirve desde memoria el mismo valor. Invalidación explícita para el proceso
/// que muta disponibilidad (worker de compras en S02) — vive en el mismo proceso que el cache.
/// </summary>
public sealed class AvailabilityService(
    IEventRepository repository,
    HybridCache cache,
    AvailabilityCacheOptions options,
    ILogger<AvailabilityService> logger)
{
    public async Task<CacheOutcome> GetAvailabilityAsync(string eventId, CancellationToken ct)
    {
        // El factory corre solo cuando no hay entrada válida en cache → eso define MISS.
        var fromCache = true;
        var available = await cache.GetOrCreateAsync(
            CacheKey(eventId),
            async token =>
            {
                fromCache = false;
                var evt = await repository.GetByIdAsync(eventId, token)
                          ?? throw new EventNotFoundException(eventId);
                return evt.Available;
            },
            new HybridCacheEntryOptions { Expiration = options.Ttl },
            cancellationToken: ct);
        return new CacheOutcome(available, fromCache);
    }

    /// <summary>
    /// Invalidación explícita (H-01 AC2): la llama el mismo proceso que muta la disponibilidad
    /// — en S02, el worker que procesa reserve-seats/release-seats; en S01, el comando sintético
    /// de tests/demo. La próxima lectura vuelve al almacén y ve el valor nuevo.
    /// </summary>
    public async Task InvalidateAsync(string eventId, CancellationToken ct = default)
    {
        await cache.RemoveAsync(CacheKey(eventId), ct);
        logger.LogInformation("cache de disponibilidad invalidado: {EventId}", eventId);
    }

    internal static string CacheKey(string eventId) => $"catalog:availability:{eventId}";
}
