using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// End-to-end de la API del Catálogo contra el emulador real: el Program completo
/// arranca (tabla + seed condicional) y se ejercitan los contratos HTTP del plan §2
/// (AC1 MISS→HIT, AC3 lista ≥2, ProblemDetails 400/404 sin internals, invalidación).
/// </summary>
public sealed class CatalogApiTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static async Task<string> FirstSeededEventId(HttpClient client)
    {
        var events = await client.GetFromJsonAsync<JsonElement[]>("/events");
        Assert.NotNull(events);
        Assert.True(events.Length > 0, "no hay eventos sembrados");
        return events[0].GetProperty("id").GetString()!;
    }

    [SkippableFact]
    public async Task Events_lists_at_least_two_seeded_events()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var events = await client.GetFromJsonAsync<JsonElement[]>("/events");

        Assert.NotNull(events);
        Assert.True(events.Length >= 2, $"se esperaban ≥2 eventos, hubo {events.Length} (H-01 AC3)");
        foreach (var evt in events)
        {
            Assert.True(evt.TryGetProperty("id", out _), "falta 'id'");
            Assert.True(evt.TryGetProperty("name", out _), "falta 'name'");
            Assert.True(evt.TryGetProperty("date", out _), "falta 'date'");
            Assert.True(evt.TryGetProperty("price", out _), "falta 'price'");
            Assert.True(evt.TryGetProperty("available", out _), "falta 'available'");
        }
    }

    [SkippableFact]
    public async Task Availability_miss_then_hit_with_same_value_and_headers()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        var id = await FirstSeededEventId(client);

        var first = await client.GetAsync($"/events/{id}/availability");
        var second = await client.GetAsync($"/events/{id}/availability");
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("MISS", first.Headers.GetValues("X-Cache").Single()); // H-01 AC1
        Assert.Equal("HIT", second.Headers.GetValues("X-Cache").Single());
        Assert.Equal(firstBody, secondBody); // misma disponibilidad correcta

        var body = JsonDocument.Parse(secondBody).RootElement;
        Assert.Equal(id, body.GetProperty("event_id").GetString()); // contrato snake_case
        Assert.True(body.GetProperty("available").GetInt32() >= 0);
    }

    [SkippableFact]
    public async Task Availability_unknown_event_returns_404_problem_details_without_internals()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/events/evt-does-not-exist/availability");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // NFR-4
        var problem = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Equal("event-not-found", problem.GetProperty("type").GetString());
        Assert.False(raw.Contains("at System"), "no debe filtrar stack traces");
        Assert.False(raw.Contains("C:\\repos"), "no debe filtrar rutas locales");
    }

    [SkippableFact]
    public async Task Availability_invalid_id_returns_400_problem_details()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/events/BAD_ID/availability");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // NFR-1/NFR-4
        var problem = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("invalid-event-id", problem.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task Invalidate_endpoint_flips_cache_back_to_miss()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        var id = await FirstSeededEventId(client);

        _ = await client.GetAsync($"/events/{id}/availability"); // MISS → poblar
        var hit = await client.GetAsync($"/events/{id}/availability");
        Assert.Equal("HIT", hit.Headers.GetValues("X-Cache").Single());

        var invalidate = await client.PostAsync($"/internal/events/{id}/invalidate-cache", content: null);
        Assert.Equal(HttpStatusCode.NoContent, invalidate.StatusCode);

        var after = await client.GetAsync($"/events/{id}/availability");
        Assert.Equal("MISS", after.Headers.GetValues("X-Cache").Single()); // H-01 AC2 (mecanismo)
    }

    [SkippableFact]
    public async Task Invalidate_invalid_id_returns_400()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/internal/events/BAD_ID/invalidate-cache", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
