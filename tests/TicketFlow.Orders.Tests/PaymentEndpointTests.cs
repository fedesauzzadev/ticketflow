using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Orders.Core.Infrastructure;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Contrato HTTP del hold contra el emulador real (in-proc): POST /purchases con "hold" (D-0018 §1),
/// expires_at observable en GET (§2) y POST /purchases/{id}/payment con sus 400/404/409/202/200 (§3).
/// El avance a SEATS_RESERVED se siembra directo por el repositorio — el flujo completo con colas
/// reales está en SagaEndToEndTests y la demo.
/// </summary>
[Collection("orders-saga")] // su factory levanta hosted services reales (relay + sweeper): serializada
public sealed class PaymentEndpointTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static readonly JsonSerializerOptions Json =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Body con "hold": &lt;holdValue&gt; (D-0018 §1) — el campo crudo viaja tal cual al test.</summary>
    private static HttpContent HoldBody(string holdValue = "true") =>
        new StringContent(
            "{\"event_id\":\"evt-api-test\",\"seats\":1,\"hold\":" + holdValue +
            ",\"card\":{\"pan\":\"4111111111111111\",\"holder\":\"Michael Fan\",\"expiry\":\"12/29\",\"cvv\":\"123\"}}",
            Encoding.UTF8, "application/json");

    private static async Task<string> CreateHoldAtSeatsReservedAsync(
        WebApplicationFactory<Program> factory, long expiresAt, CancellationToken ct)
    {
        var repo = factory.Services.GetRequiredService<DynamoDbPurchaseRepository>();
        var purchase = Purchase.Create(PurchaseIds.New(), "evt-api-test", 1, "4111111111111111",
            "pay-test-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, hold: true, owner: AuthTestSupport.Sub());
        await repo.CreateAsync(purchase, ct);
        Assert.True(await repo.TryHoldReserveAsync(purchase.PurchaseId, expiresAt, 120.50m, ct));
        return purchase.PurchaseId;
    }

    [SkippableFact]
    public async Task Post_purchase_with_hold_true_creates_201_and_get_shows_no_expiry_yet()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        var key = "pay-test-" + Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        using var response = await client.PostAsync("/purchases", HoldBody());
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var purchaseId = JsonDocument.Parse(raw).RootElement.GetProperty("purchase_id").GetString()!;

        // PENDING esperando la reserva: expires_at aún no existe (nace en seats-reserved, D-0017 §1).
        var fetched = await client.GetFromJsonAsync<JsonElement>($"/purchases/{purchaseId}");
        Assert.Equal("PENDING", fetched.GetProperty("status").GetString());
        Assert.False(fetched.TryGetProperty("expires_at", out _));
    }

    [SkippableFact]
    public async Task Post_purchase_with_non_boolean_hold_returns_400_invalid_purchase_request()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        client.DefaultRequestHeaders.Add("Idempotency-Key", "pay-test-" + Guid.NewGuid().ToString("N"));

        using var response = await client.PostAsync("/purchases", HoldBody(holdValue: "\"yes\""));
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("invalid-purchase-request", body.GetProperty("type").GetString()); // D-0018 §1
        Assert.Contains("hold", body.GetProperty("detail").GetString());
    }

    [SkippableFact]
    public async Task Get_purchase_exposes_expires_at_only_for_holds()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(90).ToUnixTimeSeconds();

        var holdId = await CreateHoldAtSeatsReservedAsync(factory, expiresAt, ct);
        var hold = await client.GetFromJsonAsync<JsonElement>($"/purchases/{holdId}");
        Assert.Equal("SEATS_RESERVED", hold.GetProperty("status").GetString());
        var exposed = DateTimeOffset.Parse(hold.GetProperty("expires_at").GetString()!);
        Assert.Equal(expiresAt, exposed.ToUnixTimeSeconds()); // AC1: expiración observable ISO-8601

        // Sin hold (camino V1): nunca aparece expires_at.
        var repo = factory.Services.GetRequiredService<DynamoDbPurchaseRepository>();
        var plain = Purchase.Create(PurchaseIds.New(), "evt-api-test", 1, "4111111111111111",
            "pay-test-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, owner: AuthTestSupport.Sub());
        await repo.CreateAsync(plain, ct);
        var plainBody = await client.GetFromJsonAsync<JsonElement>($"/purchases/{plain.PurchaseId}");
        Assert.False(plainBody.TryGetProperty("expires_at", out _));
    }

    [SkippableFact]
    public async Task Payment_endpoint_validates_id_and_404s_unknown_purchase()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();

        using var badId = await client.PostAsync("/purchases/no-es-un-id/payment", null);
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Equal("invalid-purchase-id",
            JsonDocument.Parse(await badId.Content.ReadAsStringAsync()).RootElement.GetProperty("type").GetString());

        using var missing = await client.PostAsync($"/purchases/{PurchaseIds.New()}/payment", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("purchase-not-found",
            JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement.GetProperty("type").GetString());
    }

    [SkippableFact]
    public async Task Payment_endpoint_claims_hold_202_then_replay_200()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(60).ToUnixTimeSeconds();
        var id = await CreateHoldAtSeatsReservedAsync(factory, expiresAt, ct);

        using var claimed = await client.PostAsync($"/purchases/{id}/payment", null);
        var raw = await claimed.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Accepted, claimed.StatusCode); // 202: la saga V1 sigue sola
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(id, body.GetProperty("purchase_id").GetString());
        Assert.Equal("SEATS_RESERVED", body.GetProperty("status").GetString());
        Assert.Equal(expiresAt,
            DateTimeOffset.Parse(body.GetProperty("expires_at").GetString()!).ToUnixTimeSeconds());

        using var replay = await client.PostAsync($"/purchases/{id}/payment", null);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // D-0018 §3: replay ⇒ 200 sin efectos
        Assert.Equal("SEATS_RESERVED",
            JsonDocument.Parse(await replay.Content.ReadAsStringAsync())
                .RootElement.GetProperty("status").GetString());
    }

    [SkippableFact]
    public async Task Payment_endpoint_rejects_expired_hold_409_with_actionable_detail()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        var id = await CreateHoldAtSeatsReservedAsync(factory,
            DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(), ct);
        var repo = factory.Services.GetRequiredService<DynamoDbPurchaseRepository>();
        Assert.True(await repo.TryExpireAsync(id, "reserva expirada sin pago", ct));

        using var late = await client.PostAsync($"/purchases/{id}/payment", null);
        var raw = await late.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode); // AC4: 4xx accionable
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("hold-expired", body.GetProperty("type").GetString());
        Assert.Contains("expiró", body.GetProperty("detail").GetString());
    }

    [SkippableFact]
    public async Task Payment_endpoint_rejects_non_hold_and_pending_with_409_invalid_state()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        var repo = factory.Services.GetRequiredService<DynamoDbPurchaseRepository>();

        var pending = Purchase.Create(PurchaseIds.New(), "evt-api-test", 1, "4111111111111111",
            "pay-test-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, owner: AuthTestSupport.Sub());
        await repo.CreateAsync(pending, ct);
        using var tooEarly = await client.PostAsync($"/purchases/{pending.PurchaseId}/payment", null);
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal("invalid-state",
            JsonDocument.Parse(await tooEarly.Content.ReadAsStringAsync()).RootElement.GetProperty("type").GetString());

        var nonHold = Purchase.Create(PurchaseIds.New(), "evt-api-test", 1, "4111111111111111",
            "pay-test-" + Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, owner: AuthTestSupport.Sub());
        await repo.CreateAsync(nonHold, ct);
        await repo.TryTransitionAsync(nonHold.PurchaseId,
            PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED, ct: ct);
        using var wrongKind = await client.PostAsync($"/purchases/{nonHold.PurchaseId}/payment", null);
        Assert.Equal(HttpStatusCode.Conflict, wrongKind.StatusCode);
    }

    [SkippableFact]
    public async Task Replay_of_expired_hold_post_returns_same_purchase_without_effects()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        using var factory = NewFactory();
        var client = factory.CreateAuthClient();
        var key = "pay-test-" + Guid.NewGuid().ToString("N");

        // AC6 primera mitad: misma key + mismo body ⇒ la MISMA compra en su terminal EXPIRED.
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        using var created = await client.PostAsync("/purchases", HoldBody());
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync())
            .RootElement.GetProperty("purchase_id").GetString()!;
        var repo = factory.Services.GetRequiredService<DynamoDbPurchaseRepository>();
        Assert.True(await repo.TryHoldReserveAsync(id,
            DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds(), 120.50m, ct));
        Assert.True(await repo.TryExpireAsync(id, "reserva expirada sin pago", ct));

        using var replay = await client.PostAsync("/purchases", HoldBody());
        var raw = await replay.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var body = JsonDocument.Parse(raw).RootElement;
        Assert.Equal(id, body.GetProperty("purchase_id").GetString());
        Assert.Equal("EXPIRED", body.GetProperty("status").GetString());
        Assert.NotNull(body.GetProperty("reason").GetString());
    }
}
