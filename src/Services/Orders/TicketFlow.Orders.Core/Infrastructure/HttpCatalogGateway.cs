using System.Net.Http.Json;
using System.Text.Json;
using TicketFlow.Orders.Core.Application;

namespace TicketFlow.Orders.Core.Infrastructure;

/// <summary>
/// Peek de disponibilidad vía la API pública del Catálogo (D-0009): GET /events/{id}/availability.
/// Timeout 3 s explícito (NFR-3): es un pre-check del POST, no puede costar la mitad del
/// presupuesto de latencia (NFR-5). Fail-open ante cualquier falla de red/parseo.
/// </summary>
public sealed class HttpCatalogGateway : ICatalogAvailabilityGateway
{
    private readonly HttpClient _client;

    public HttpCatalogGateway(string? baseUrl)
    {
        // Fail-fast de config (NFR-1): una base URL inválida revienta al arranque, no por request.
        _client = new HttpClient { BaseAddress = new Uri(baseUrl ?? "http://localhost:5080"), Timeout = TimeSpan.FromSeconds(3) };
    }

    public async Task<int?> GetAvailabilityAsync(string eventId, CancellationToken ct)
    {
        try
        {
            using var response = await _client.GetAsync($"/events/{eventId}/availability", ct);
            if (!response.IsSuccessStatusCode)
                return null; // 404/5xx: el evento puede no existir aún en el Catálogo → la saga decide
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.TryGetProperty("available", out var available) &&
                   available.ValueKind == JsonValueKind.Number
                ? available.GetInt32()
                : null; // respuesta malformada: no validable
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            // TaskCanceledException aquí = timeout del HttpClient (el ct del caller se respeta:
            // si el caller cancela, la siguiente operación DDB lo propaga igual).
            return null; // Catálogo inalcanzable: fail-open (D-0009)
        }
    }
}
