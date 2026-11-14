using System.Text.RegularExpressions;

namespace TicketFlow.Catalog.Core.Domain;

/// <summary>
/// Ids de evento: kebab-case, máx 64 — seguros para URL y para clave de DynamoDB.
/// Validación compartida por la API (frontera HTTP, NFR-1) y el dominio.
/// </summary>
public static partial class CatalogEventId
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex Pattern();

    public static bool IsValid(string id) => Pattern().IsMatch(id);
}

/// <summary>
/// Evento del catálogo (agregado de lectura de S01).
/// Invariantes: id válido, nombre 1..200, fecha definida, precio ≥ 0, disponibilidad ≥ 0.
/// </summary>
public sealed record CatalogEvent(string Id, string Name, DateTimeOffset Date, decimal Price, int Available)
{
    public static CatalogEvent Create(string id, string name, DateTimeOffset date, decimal price, int available)
    {
        if (!CatalogEventId.IsValid(id))
            throw new ArgumentException($"Id de evento inválido: '{id}' (kebab-case, máx 64 caracteres).", nameof(id));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new ArgumentException("El nombre debe tener entre 1 y 200 caracteres.", nameof(name));
        if (date == default)
            throw new ArgumentException("La fecha del evento es obligatoria.", nameof(date));
        if (price < 0)
            throw new ArgumentException("El precio no puede ser negativo.", nameof(price));
        if (available < 0)
            throw new ArgumentException("La disponibilidad no puede ser negativa.", nameof(available));
        return new CatalogEvent(id, name.Trim(), date, price, available);
    }
}
