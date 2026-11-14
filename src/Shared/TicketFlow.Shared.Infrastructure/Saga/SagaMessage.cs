using System.Text.Json;

namespace TicketFlow.Shared.Infrastructure.Saga;

/// <summary>Tipos de mensaje de la saga (D-0004): comandos Órdenes→servicio, eventos servicio→Órdenes.</summary>
public static class SagaTypes
{
    // Comandos
    public const string ReserveSeats = "reserve-seats";
    public const string ReleaseSeats = "release-seats";
    public const string AuthorizePayment = "authorize-payment";
    public const string IssueTickets = "issue-tickets";

    // Eventos
    public const string SeatsReserved = "seats-reserved";
    public const string SeatsUnavailable = "seats-unavailable";
    public const string SeatsReleased = "seats-released";
    public const string PaymentApproved = "payment-approved";
    public const string PaymentDeclined = "payment-declined";
    public const string TicketsIssued = "tickets-issued";
}

/// <summary>Colas FIFO de la saga (D-0006): una de comandos por servicio + eventos hacia Órdenes.</summary>
public static class SagaQueues
{
    public const string CatalogCommands = "ticketflow-catalog-commands.fifo";
    public const string PaymentsCommands = "ticketflow-payments-commands.fifo";
    public const string TicketingCommands = "ticketflow-ticketing-commands.fifo";
    public const string OrdersEvents = "ticketflow-orders-events.fifo";

    public static readonly IReadOnlyList<string> All =
        [CatalogCommands, PaymentsCommands, TicketingCommands, OrdersEvents];
}

/// <summary>
/// Sobre del mensaje de la saga: { type, purchase_id, payload }. El payload tipado se deserializa
/// en el handler con <see cref="PayloadAs{T}"/>; si falta, la frontera lo rechaza (NFR-1).
/// </summary>
public sealed record SagaEnvelope(string Type, string PurchaseId, JsonElement? Payload)
{
    public T PayloadAs<T>()
    {
        if (Payload is not { } element)
            throw new InvalidOperationException(
                $"Mensaje de saga '{Type}' (compra {PurchaseId}) sin payload: descartado en la frontera.");
        return element.Deserialize<T>(SagaJson.Options)
               ?? throw new InvalidOperationException(
                   $"Payload de '{Type}' (compra {PurchaseId}) no deserializa como {typeof(T).Name}.");
    }
}

public static class SagaJson
{
    public static readonly JsonSerializerOptions Options =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static SagaEnvelope Deserialize(string json) =>
        JsonSerializer.Deserialize<SagaEnvelope>(json, Options)
        ?? throw new InvalidOperationException("Mensaje de saga vacío.");

    public static string Serialize<T>(string type, string purchaseId, T payload) =>
        JsonSerializer.Serialize(
            new SagaEnvelope(type, purchaseId, JsonSerializer.SerializeToElement(payload, Options)), Options);

    public static string SerializeEnvelope(SagaEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, Options);
}

// --- Contratos de la saga (comandos Órdenes → servicio) ------------------------

public sealed record ReserveSeatsCommand(string EventId, int Seats);
public sealed record ReleaseSeatsCommand(string EventId, int Seats);
public sealed record AuthorizePaymentCommand(string Pan, decimal Amount);
public sealed record IssueTicketsCommand(string EventId, int Seats);

// --- Contratos de la saga (eventos servicio → Órdenes) -------------------------

public sealed record SeatsReservedEvent(string EventId, decimal UnitPrice);
public sealed record SeatsUnavailableEvent(string EventId, string Reason);
public sealed record SeatsReleasedEvent(string EventId);
public sealed record PaymentApprovedEvent(decimal Amount);
public sealed record PaymentDeclinedEvent(string Reason);
public sealed record TicketsIssuedEvent(IReadOnlyList<string> TicketIds);
