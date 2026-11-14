using System.Text.RegularExpressions;
using TicketFlow.Orders.Core.Domain;

namespace TicketFlow.Orders.Core.Application;

/// <summary>Body del POST /purchases según el contrato del plan §2.</summary>
public sealed record CardRequest(string? Pan, string? Holder, string? Expiry, string? Cvv);

public sealed record PurchaseRequest(string? EventId, int? Seats, CardRequest? Card);

/// <summary>
/// Validación de la frontera HTTP (NFR-1): devuelve mensajes accionables por campo; vacío = válido.
/// La misma regla de id de evento que el Catálogo (duplicada a propósito: sin dependencia
/// cross-servicio por 6 líneas de regex).
/// </summary>
public static partial class PurchaseRequestValidator
{
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$")]
    private static partial Regex EventIdPattern();

    [GeneratedRegex(@"^\d{13,19}$")]
    private static partial Regex PanPattern();

    [GeneratedRegex(@"^(0[1-9]|1[0-2])/\d{2}$")]
    private static partial Regex ExpiryPattern();

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex CvvPattern();

    public const int MaxIdempotencyKeyLength = 200;

    public static string? ValidateIdempotencyKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? "el header Idempotency-Key es requerido"
        : key.Length > MaxIdempotencyKeyLength ? $"el Idempotency-Key no puede exceder {MaxIdempotencyKeyLength} caracteres"
        : null;

    public static IReadOnlyList<string> Validate(PurchaseRequest request)
    {
        var errors = new List<string>();

        var eventId = request.EventId ?? "";
        if (eventId.Length == 0) errors.Add("event_id es requerido");
        else if (!EventIdPattern().IsMatch(eventId)) errors.Add("event_id inválido (kebab-case, máx 64 caracteres)");

        if (request.Seats is null or < 1 or > 8)
            errors.Add("seats debe ser un entero entre 1 y 8");

        var card = request.Card;
        if (card is null)
        {
            errors.Add("card es requerido");
        }
        else
        {
            if (card.Pan is null || !PanPattern().IsMatch(card.Pan))
                errors.Add("card.pan debe tener entre 13 y 19 dígitos");
            if (card.Holder is null || card.Holder.Trim().Length is < 2 or > 80)
                errors.Add("card.holder debe tener entre 2 y 80 caracteres");
            if (card.Expiry is null || !ExpiryPattern().IsMatch(card.Expiry))
                errors.Add("card.expiry debe tener formato MM/YY con mes 01..12");
            if (card.Cvv is null || !CvvPattern().IsMatch(card.Cvv))
                errors.Add("card.cvv debe tener exactamente 3 dígitos");
        }

        return errors;
    }
}
