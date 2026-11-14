using Amazon.DynamoDBv2;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Contrato HTTP del servicio Órdenes contra el emulador real: POST /purchases (201 inmediato
/// con purchase_id, D-0004) y GET /purchases/{id} (H-02 AC4), con ProblemDetails sin internals (NFR-4).
/// </summary>
public sealed class OrdersApiTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static HttpContent PurchaseBody(string mediaType = "application/json", int seats = 1,
        string pan = "4111111111111111") =>
        new StringContent(
            JsonSerializer.Serialize(new
            {
                event_id = "evt-api-test",
                seats,
                card = new { pan, holder = "Michael Jackson", expiry = "12/29", cvv = "123" },
            }, Json), Encoding.UTF8, mediaType);

    [SkippableFact]
    public async Task Post_purchase_returns_201_with_id_and_pending_status()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key",
            "api-test-" + Guid.NewGuid().ToString("N")); // único por corrida: el claim persiste en el emulador

        using var response = await client.PostAsync("/purchases", PurchaseBody());
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(raw).RootElement;
        var purchaseId = body.GetProperty("purchase_id").GetString()!;
        Assert.Matches(@"^pur-[0-9a-f]{32}$", purchaseId);
        Assert.Equal("PENDING", body.GetProperty("status").GetString()); // H-02 AC4: PENDING visible
        Assert.Equal($"/purchases/{purchaseId}", response.Headers.Location?.ToString());

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/purchases/{purchaseId}");
        Assert.Equal("PENDING", fetched.GetProperty("status").GetString());
        Assert.True(fetched.TryGetProperty("created_at", out _));
    }

    [SkippableFact]
    public async Task Post_without_idempotency_key_returns_400_problem_details()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Remove("Idempotency-Key");

        using var content = PurchaseBody();
        using var response = await client.PostAsync("/purchases", content);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("invalid-idempotency-key", body.GetProperty("type").GetString()); // ProblemDetails (estilo S01)
        Assert.Contains("Idempotency-Key", body.GetProperty("detail").GetString());
    }

    [SkippableFact]
    public async Task Post_with_invalid_body_returns_400_actionable_detail()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "k-1");

        using var invalid = new StringContent(
            """{"event_id":"evt-api-test","seats":99,"card":{"pan":"12","holder":"","expiry":"13/29","cvv":"1"}}""",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/purchases", invalid);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var detail = JsonDocument.Parse(raw).RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("seats", detail);
        Assert.Contains("pan", detail); // accionable por campo, sin internals (NFR-4)
    }

    [SkippableFact]
    public async Task Post_with_malformed_json_returns_400_without_crashing()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "k-2");

        using var garbage = new StringContent("{ no es json ]", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/purchases", garbage);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    public async Task Post_non_json_content_type_returns_415_without_side_effects()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();
        var key = "api-test-" + Guid.NewGuid().ToString("N"); // único por corrida (L-008)
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        // JSON válido con Content-Type equivocado (QA F-04 / H-10 AC1) — mismo body que PurchaseBody().
        using var plainText = PurchaseBody("text/plain");
        using var rejected = await client.PostAsync("/purchases", plainText);
        var raw = await rejected.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
        var problem = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("unsupported-media-type", problem.GetProperty("type").GetString());
        Assert.Contains("application/json", problem.GetProperty("detail").GetString());

        // Cero efectos: la key no se consumió — el mismo body con application/json es 201 (no replay 200).
        using var valid = PurchaseBody();
        using var created = await client.PostAsync("/purchases", valid);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [SkippableFact]
    public async Task Get_unknown_purchase_returns_404_and_bad_format_returns_400()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var notFound = await client.GetAsync("/purchases/pur-00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var badId = await client.GetAsync("/purchases/not-a-purchase-id");
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
    }
}
