namespace TicketFlow.Catalog.Core.Application;

/// <summary>Resultado idempotente de reserve-seats (primitiva D-0005: decremento condicional).</summary>
public enum ReservationOutcome
{
    /// <summary>Se reclamó la reserva y se decrementó la disponibilidad.</summary>
    Reserved,
    /// <summary>Comando duplicado de una reserva ya concretada: NO se decrementa de nuevo (hook H-03 AC2).</summary>
    DuplicateReserved,
    /// <summary>Sin disponibilidad suficiente (o evento inexistente): nada cambió.</summary>
    Unavailable,
}

public sealed record ReserveResult(ReservationOutcome Outcome, decimal UnitPrice);

/// <summary>Resultado idempotente de release-seats (compensación, H-02 AC3).</summary>
public enum ReleaseOutcome
{
    /// <summary>Se reclamó la liberación y se revirtió el decremento.</summary>
    Released,
    /// <summary>Ya estaba liberada (o nunca reservada): NO se incrementa de nuevo.</summary>
    Duplicate,
}

/// <summary>
/// Puerto de reserva/liberación de asientos para la saga (S02, D-0004/D-0005). Separado de
/// IEventRepository para no romper su contrato de lectura: DynamoDbEventRepository implementa ambos.
/// </summary>
public interface ISeatReservationStore
{
    Task<ReserveResult> ReserveAsync(string eventId, string purchaseId, int seats, CancellationToken ct);

    Task<ReleaseOutcome> ReleaseAsync(string eventId, string purchaseId, int seats, CancellationToken ct);
}

