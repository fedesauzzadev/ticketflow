using System.Globalization;
using TicketFlow.Catalog.Core.Domain;

namespace TicketFlow.Catalog.Core.Application;

/// <summary>
/// Seed inicial del catálogo. Formato de SEED_EVENTS (env):
/// "id|nombre|fecha-ISO-8601|precio|disponibilidad;..." — cada entrada se valida al
/// arranque con mensaje accionable (NFR-1: la config es una frontera de confianza).
/// </summary>
public static class SeedEvents
{
    public static IReadOnlyList<CatalogEvent> Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Default();

        var events = spec
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseEntry)
            .ToList();

        if (events.Count == 0)
            throw new ArgumentException("SEED_EVENTS no contiene ninguna entrada.");
        if (events.Select(e => e.Id).Distinct().Count() != events.Count)
            throw new ArgumentException("SEED_EVENTS contiene ids de evento duplicados.");
        return events;
    }

    private static CatalogEvent ParseEntry(string entry)
    {
        var parts = entry.Split('|');
        if (parts.Length != 5)
            throw new ArgumentException(
                $"Entrada de seed mal formada (se esperan 5 campos 'id|nombre|fecha|precio|disponibilidad'): '{entry}'");
        if (!DateTimeOffset.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new ArgumentException($"Fecha de seed inválida (ISO-8601, p. ej. 2027-03-12T21:00:00Z): '{parts[2]}'");
        if (!decimal.TryParse(parts[3], NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
            throw new ArgumentException($"Precio de seed inválido: '{parts[3]}'");
        if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var available))
            throw new ArgumentException($"Disponibilidad de seed inválida: '{parts[4]}'");
        return CatalogEvent.Create(parts[0].Trim(), parts[1].Trim(), date, price, available);
    }

    /// <summary>Defaults de desarrollo (los usa run-local.ps1 y el arranque sin SEED_EVENTS).</summary>
    public static IReadOnlyList<CatalogEvent> Default() =>
    [
        CatalogEvent.Create("evt-mj-tribute-night", "Michael Jackson Tribute Night",
            new DateTimeOffset(2027, 3, 12, 21, 0, 0, TimeSpan.Zero), 120.50m, 500),
        CatalogEvent.Create("evt-legacy-tour", "Legacy World Tour (Orquestal)",
            new DateTimeOffset(2027, 4, 3, 20, 0, 0, TimeSpan.Zero), 85.00m, 250),
    ];
}
