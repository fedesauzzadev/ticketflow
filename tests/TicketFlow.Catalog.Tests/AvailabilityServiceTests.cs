using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Tests unitarios del cache-aside (D-0007): MISS→HIT mismo valor (H-01 AC1),
/// invalidación explícita con valor nuevo (H-01 AC2, comando sintético), TTL,
/// y no-cacheo de errores. Repo fake en memoria; HybridCache real (L1 in-memory).
/// </summary>
public sealed class AvailabilityServiceTests
{
    private sealed class FakeRepository : IEventRepository
    {
        public Dictionary<string, CatalogEvent> Store { get; } = new();
        public int Reads { get; private set; }

        public Task<IReadOnlyList<CatalogEvent>> ListAsync(CancellationToken ct) =>
            Task.FromResult((IReadOnlyList<CatalogEvent>)Store.Values.ToList());

        public Task<CatalogEvent?> GetByIdAsync(string eventId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Store.TryGetValue(eventId, out var evt) ? evt : null);
        }
    }

    private static AvailabilityService CreateService(FakeRepository repo, TimeSpan? ttl = null)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        return new AvailabilityService(
            repo, cache,
            new AvailabilityCacheOptions { Ttl = ttl ?? TimeSpan.FromSeconds(30) },
            NullLogger<AvailabilityService>.Instance);
    }

    private static CatalogEvent Event(string id, int available) =>
        CatalogEvent.Create(id, "Test Event", new DateTimeOffset(2027, 1, 1, 20, 0, 0, TimeSpan.Zero), 10m, available);

    [Fact]
    public async Task First_read_is_miss_second_is_hit_with_same_value()
    {
        var repo = new FakeRepository();
        repo.Store["evt-test"] = Event("evt-test", 500);
        var svc = CreateService(repo);

        var first = await svc.GetAvailabilityAsync("evt-test", CancellationToken.None);
        var second = await svc.GetAvailabilityAsync("evt-test", CancellationToken.None);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(500, first.Available);
        Assert.Equal(second.Available, first.Available);
        Assert.Equal(1, repo.Reads); // el HIT no tocó el repositorio
    }

    [Fact]
    public async Task Invalidate_forces_miss_and_returns_new_value()
    {
        // Comando sintético de mutación de disponibilidad (la compra real llega en S02).
        var repo = new FakeRepository();
        repo.Store["evt-test"] = Event("evt-test", 500);
        var svc = CreateService(repo);

        await svc.GetAvailabilityAsync("evt-test", CancellationToken.None); // MISS → poblar
        await svc.GetAvailabilityAsync("evt-test", CancellationToken.None); // HIT

        repo.Store["evt-test"] = Event("evt-test", 499); // el "worker" mutó el estado
        await svc.InvalidateAsync("evt-test", CancellationToken.None);

        var after = await svc.GetAvailabilityAsync("evt-test", CancellationToken.None);
        Assert.False(after.FromCache);
        Assert.Equal(499, after.Available);
        Assert.Equal(2, repo.Reads);
    }

    [Fact]
    public async Task Invalidate_for_unknown_event_is_a_noop()
    {
        var svc = CreateService(new FakeRepository());
        await svc.InvalidateAsync("evt-never-cached", CancellationToken.None); // no lanza
    }

    [Fact]
    public async Task Expired_ttl_reads_through_again()
    {
        var repo = new FakeRepository();
        repo.Store["evt-test"] = Event("evt-test", 42);
        var svc = CreateService(repo, ttl: TimeSpan.FromMilliseconds(100));

        await svc.GetAvailabilityAsync("evt-test", CancellationToken.None);
        await Task.Delay(300);
        var after = await svc.GetAvailabilityAsync("evt-test", CancellationToken.None);

        Assert.False(after.FromCache); // expiró → MISS aunque nadie invalidó
        Assert.Equal(42, after.Available);
    }

    [Fact]
    public async Task Missing_event_throws_and_is_not_cached()
    {
        var repo = new FakeRepository();
        var svc = CreateService(repo);

        await Assert.ThrowsAsync<EventNotFoundException>(
            () => svc.GetAvailabilityAsync("evt-missing", CancellationToken.None));

        repo.Store["evt-missing"] = Event("evt-missing", 7); // aparece después del 404
        var after = await svc.GetAvailabilityAsync("evt-missing", CancellationToken.None);
        Assert.False(after.FromCache); // el fallo anterior no quedó cacheado
        Assert.Equal(7, after.Available);
    }
}
