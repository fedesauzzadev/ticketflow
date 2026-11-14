using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;
using TicketFlow.Catalog.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Integración contra el emulador AWS REAL (:9297): repositorio DynamoDB (D-0005),
/// seed condicional (H-01 AC4) e invalidación de cache con valor nuevo desde el
/// almacén (H-01 AC2, comando sintético — la compra real llega en S02).
/// </summary>
public sealed class DynamoDbIntegrationTests
{
    private static DynamoDbEventRepository NewRepo() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    private static string NewId() => "it-" + Guid.NewGuid().ToString("N")[..12];

    private static CatalogEvent Event(string id, int available) =>
        CatalogEvent.Create(id, "Integration Event",
            new DateTimeOffset(2027, 5, 1, 20, 0, 0, TimeSpan.Zero), 42m, available);

    [SkippableFact]
    public async Task Conditional_seed_does_not_reset_state_on_reinit()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var id = NewId();

        await repo.InitializeAsync([Event(id, 10)], CancellationToken.None);
        await repo.PutAsync(Event(id, 7), CancellationToken.None);            // el estado evolucionó
        await repo.InitializeAsync([Event(id, 10)], CancellationToken.None);  // "reinicio del microservicio"

        var stored = await repo.GetByIdAsync(id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(7, stored.Available); // el re-seed NO pisó la disponibilidad (H-01 AC4)
    }

    [SkippableFact]
    public async Task Put_then_get_roundtrips_all_fields()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var evt = CatalogEvent.Create(NewId(), "Roundtrip Show",
            new DateTimeOffset(2027, 6, 15, 21, 30, 0, TimeSpan.Zero), 99.99m, 123);

        await repo.InitializeAsync([], CancellationToken.None); // asegura la tabla
        await repo.PutAsync(evt, CancellationToken.None);

        var stored = await repo.GetByIdAsync(evt.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(evt, stored);
    }

    [SkippableFact]
    public async Task Get_by_id_returns_null_for_missing_event()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        await repo.InitializeAsync([], CancellationToken.None);
        Assert.Null(await repo.GetByIdAsync(NewId(), CancellationToken.None));
    }

    [SkippableFact]
    public async Task Cache_invalidation_reflects_new_value_from_dynamodb()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var repo = NewRepo();
        var id = NewId();
        await repo.InitializeAsync([Event(id, 500)], CancellationToken.None);

        var services = new ServiceCollection();
        services.AddHybridCache();
        var cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        var svc = new AvailabilityService(
            repo, cache, new AvailabilityCacheOptions(), NullLogger<AvailabilityService>.Instance);

        var miss = await svc.GetAvailabilityAsync(id, CancellationToken.None);
        var hit = await svc.GetAvailabilityAsync(id, CancellationToken.None);
        Assert.False(miss.FromCache);
        Assert.True(hit.FromCache);
        Assert.Equal(500, hit.Available);

        await repo.PutAsync(Event(id, 499), CancellationToken.None); // mutación de disponibilidad
        await svc.InvalidateAsync(id, CancellationToken.None);

        var after = await svc.GetAvailabilityAsync(id, CancellationToken.None);
        Assert.False(after.FromCache);
        Assert.Equal(499, after.Available); // valor NUEVO del almacén, no el cacheado
    }
}
