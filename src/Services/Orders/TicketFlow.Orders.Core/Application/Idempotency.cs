using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TicketFlow.Orders.Core.Application;

/// <summary>
/// Registro reclamado por un Idempotency-Key (H-03 AC1): la key es dueña de un único
/// purchase_id y del hash del body con el que se usó.
/// </summary>
public sealed record IdempotencyClaim(string PurchaseId, string RequestHash);

/// <summary>
/// Store de idempotencia HTTP (D-0009): peek + claim condicional por key. El claim es un
/// PutItem con attribute_not_exists — la primera request que gana crea la compra; las
/// demás reciben el registro existente (replay o mismatch).
/// </summary>
public interface IIdempotencyStore
{
    Task InitializeAsync(CancellationToken ct);

    Task<IdempotencyClaim?> FindAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>true si esta llamada ganó el derecho a crear la compra; false si la key ya estaba usada.</summary>
    Task<bool> TryClaimAsync(string idempotencyKey, string requestHash, string purchaseId, CancellationToken ct);
}

/// <summary>
/// Hash canónico del body del POST (D-0009): SHA-256 de la re-serialización snake_case del
/// request validado — bodies semánticamente iguales (distinto whitespace/orden) hashean igual.
/// </summary>
public static class PurchaseBodyHash
{
    private static readonly JsonSerializerOptions Canonical =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string Of(PurchaseRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request, Canonical)))).ToLowerInvariant();
}
