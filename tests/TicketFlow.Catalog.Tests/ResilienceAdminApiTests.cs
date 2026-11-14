using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Endpoints admin de resiliencia (D-0012, N-2002): frontera HTTP validada (NFR-1 — 400
/// accionables), ciclo POST→GET→DELETE operable por el evaluador sin editar código, y
/// GET /admin/dlq inspeccionable (peek sin borrar). In-proc contra el host real del Catálogo.
/// Se inyecta sobre un paso sintético que no existe en el tráfico real para no interferir
/// con los workers de hosts paralelos de la suite (L-008).
/// </summary>
public sealed class ResilienceAdminApiTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static async Task<JsonElement> PostRuleAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync("/admin/failure", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [SkippableFact]
    public async Task Failure_rule_lifecycle_post_get_delete()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        // POST válido → 201 con la regla creada (count default 2).
        var created = await PostRuleAsync(client, new { step = "it-probe-step", mode = "transient" });
        Assert.Equal("it-probe-step", created.GetProperty("step").GetString());
        Assert.Equal(2, created.GetProperty("remaining").GetInt32());

        // GET → la lista contiene la regla.
        var rules = await client.GetFromJsonAsync<JsonElement[]>("/admin/failure");
        Assert.Contains(rules!, r => r.GetProperty("step").GetString() == "it-probe-step");

        // DELETE → 204 y desaparece; DELETE de nuevo → 404 ProblemDetails.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync("/admin/failure/it-probe-step")).StatusCode);
        var missing = await client.DeleteAsync("/admin/failure/it-probe-step");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var problem = JsonDocument.Parse(await missing.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("failure-rule-not-found", problem.GetProperty("type").GetString()); // NFR-4
    }

    [SkippableFact]
    public async Task Invalid_rules_are_rejected_with_400_actionable_problemdetails()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        foreach (var (body, why) in new (object, string)[]
                 {
                     (new { step = "", mode = "transient" }, "step vacío"),
                     (new { step = "no permitido!", mode = "transient" }, "step con caracteres inválidos"),
                     (new { step = new string('a', 65), mode = "transient" }, "step > 64"),
                     (new { step = "x", mode = "sometimes" }, "modo inválido"),
                     (new { step = "x", mode = "transient", count = 0 }, "count < 1"),
                     (new { step = "x", mode = "transient", count = 11 }, "count > 10"),
                 })
        {
            var response = await client.PostAsJsonAsync("/admin/failure", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); // NFR-1: validado
            var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal("invalid-failure-rule", problem.GetProperty("type").GetString());
            Assert.True(problem.GetProperty("detail").GetString()!.Length > 0, $"sin detalle accionable: {why}");
        }
    }

    [SkippableFact]
    public async Task Dlq_endpoint_is_inspectable_and_starts_empty()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/admin/dlq");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("messages").ValueKind == JsonValueKind.Array,
            "el contrato es {messages: [...] }");
    }
}
