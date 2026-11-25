using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Idempotencia HTTP observable (H-03 AC1/AC3, D-0009) contra el emulador real: replay con
/// misma key + mismo body ⇒ misma compra sin efectos; misma key + body distinto ⇒ 422;
/// key nueva con asientos agotados ⇒ 422 sin consumir la key. El gateway del Catálogo se
/// reemplaza por un fake determinista (la cadena HTTP real se demuestra en la evidencia S03).
/// </summary>
public sealed class OrdersIdempotencyApiTests
{
    private sealed class FakeCatalogGateway : ICatalogAvailabilityGateway
    {
        public int? Available { get; set; }
        public Task<int?> GetAvailabilityAsync(string eventId, CancellationToken ct) => Task.FromResult(Available);
    }

    private static WebApplicationFactory<Program> NewFactory(FakeCatalogGateway gateway) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ICatalogAvailabilityGateway>();
            services.AddSingleton<ICatalogAvailabilityGateway>(gateway);
        }));

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static HttpContent Body(int seats = 1, string pan = "4111111111111111",
        string eventId = "evt-idem-test") =>
        new StringContent(JsonSerializer.Serialize(new
        {
            event_id = eventId,
            seats,
            card = new { pan, holder = "Eval Evaluador", expiry = "12/29", cvv = "123" },
        }, Json), Encoding.UTF8, "application/json");

    private static string NewKey() => "it-key-" + Guid.NewGuid().ToString("N");

    [SkippableFact]
    public async Task Replay_same_key_and_body_returns_same_purchase_without_new_effects()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory(new FakeCatalogGateway { Available = null }); // fail-open
        var client = factory.CreateAuthClient();
        var key = NewKey();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var created = await client.PostAsync("/purchases", Body());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement;
        var purchaseId = first.GetProperty("purchase_id").GetString()!;

        // Replay con MISMA key y MISMO body: la misma compra, sin crear nada nuevo.
        using var replay = await client.PostAsync("/purchases", Body());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var second = JsonDocument.Parse(await replay.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(purchaseId, second.GetProperty("purchase_id").GetString());
        Assert.Equal("PENDING", second.GetProperty("status").GetString());
        Assert.Null(replay.Headers.Location); // nada nuevo fue creado
    }

    [SkippableFact]
    public async Task Same_key_with_different_body_returns_422_and_keeps_original_purchase()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory(new FakeCatalogGateway());
        var client = factory.CreateAuthClient();
        var key = NewKey();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var created = await client.PostAsync("/purchases", Body(seats: 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var purchaseId = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("purchase_id").GetString()!;

        using var conflict = await client.PostAsync("/purchases", Body(seats: 2));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflict.StatusCode);
        var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("idempotency-key-reused", problem.GetProperty("type").GetString());
        Assert.False(problem.TryGetProperty("purchase_id", out _)); // no creó nada

        // La compra original quedó intacta.
        var original = await client.GetFromJsonAsync<JsonElement>($"/purchases/{purchaseId}");
        Assert.Equal("PENDING", original.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task Sold_out_new_key_returns_422_without_consuming_the_key()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var gateway = new FakeCatalogGateway { Available = 0 }; // evento agotado
        using var factory = NewFactory(gateway);
        var client = factory.CreateAuthClient();
        var key = NewKey();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var soldOut = await client.PostAsync("/purchases", Body());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, soldOut.StatusCode);
        var problem = JsonDocument.Parse(await soldOut.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("seats-unavailable", problem.GetProperty("type").GetString());
        Assert.Contains("disponibilidad", problem.GetProperty("detail").GetString());
        Assert.False(problem.TryGetProperty("purchase_id", out _)); // sin efectos parciales

        // El 422 NO consumió la key: cuando hay asientos, reintentar con la misma key crea la compra.
        gateway.Available = null; // Catálogo no validable → fail-open
        using var retry = await client.PostAsync("/purchases", Body());
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        var purchaseId = JsonDocument.Parse(await retry.Content.ReadAsStringAsync())
            .RootElement.GetProperty("purchase_id").GetString();
        Assert.Matches(@"^pur-[0-9a-f]{32}$", purchaseId);
    }

    [SkippableFact]
    public async Task Claim_without_purchase_is_completed_by_the_replay_with_the_claimed_id()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        // Simula la ventana de crash entre claim y create (D-0009): la key quedó registrada sin compra.
        var store = new DynamoDbIdempotencyStore(
            EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));
        await store.InitializeAsync(ct);
        var key = NewKey();
        // El hash del claim debe ser el del body que se va a POSTear (Body(): holder "Eval Evaluador").
        var request = new PurchaseRequest("evt-heal-test", 1,
            new CardRequest("4111111111111111", "Eval Evaluador", "12/29", "123"));
        var claimedId = PurchaseIds.New();
        Assert.True(await store.TryClaimAsync($"{AuthTestSupport.Sub()}:{key}", PurchaseBodyHash.Of(request), claimedId, ct));

        using var factory = NewFactory(new FakeCatalogGateway());
        var client = factory.CreateAuthClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var response = await client.PostAsync("/purchases", Body(eventId: "evt-heal-test"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(claimedId, body.GetProperty("purchase_id").GetString()); // el claim es dueño del id

        var purchase = await client.GetFromJsonAsync<JsonElement>($"/purchases/{claimedId}");
        Assert.Equal("PENDING", purchase.GetProperty("status").GetString());
    }
}
