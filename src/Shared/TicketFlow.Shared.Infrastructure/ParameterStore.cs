using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace TicketFlow.Shared.Infrastructure;

/// <summary>Nombres en Parameter Store (D-0024). Jerarquía /ticketflow/&lt;área&gt;/&lt;clave&gt;.</summary>
public static class ParameterNames
{
    public const string Root = "/ticketflow/";
    public const string UserPoolId = Root + "auth/user-pool-id";
    public const string ClientId = Root + "auth/client-id";
    public const string HoldTtlSeconds = Root + "orders/hold-ttl-seconds";
}

public static class ParameterStore
{
    /// <summary>Lee todos los parámetros bajo la raíz (paginado: la API devuelve de a 10).</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        IAmazonSimpleSystemsManagement ssm, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        string? next = null;
        do
        {
            var page = await ssm.GetParametersByPathAsync(new GetParametersByPathRequest
            {
                Path = ParameterNames.Root.TrimEnd('/'),
                Recursive = true,
                WithDecryption = true, // sin efecto en String; descifra los SecureString
                NextToken = next,
            }, ct);
            foreach (var p in page.Parameters ?? []) result[p.Name] = p.Value;
            next = page.NextToken;
        } while (!string.IsNullOrEmpty(next));
        return result;
    }

    public static string Required(this IReadOnlyDictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException(
                $"Falta el parámetro '{name}' en Parameter Store. Ejecutá scripts/provision.ps1 (o terraform apply).");
}
