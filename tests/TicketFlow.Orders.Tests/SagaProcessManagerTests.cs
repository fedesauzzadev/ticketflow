using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Tests unitarios del process manager (D-0004) — sin red: repo y outbox fake. Cubren la máquina
/// de estados de la saga completa: flujo feliz hasta TICKETS_ISSUED, compensación (payment-declined
/// ⇒ release-seats ⇒ FAILED con motivo), fallo de reserva y guards contra duplicados/desorden.
/// </summary>
public sealed class SagaProcessManagerTests
{
    private sealed class FakePurchaseRepository : IPurchaseRepository
    {
        public Dictionary<string, Purchase> Store { get; } = [];

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task CreateAsync(Purchase purchase, CancellationToken ct)
        {
            Store[purchase.PurchaseId] = purchase;
            return Task.CompletedTask;
        }

        public Task<Purchase?> GetAsync(string purchaseId, CancellationToken ct) =>
            Task.FromResult(Store.GetValueOrDefault(purchaseId));

        public Task<bool> TryTransitionAsync(string purchaseId, PurchaseStatus from, PurchaseStatus to,
            string? reason = null, IReadOnlyList<string>? ticketIds = null, CancellationToken ct = default)
        {
            if (!Store.TryGetValue(purchaseId, out var purchase) || purchase.Status != from)
                return Task.FromResult(false);
            Store[purchaseId] = purchase with
            {
                Status = to,
                FailureReason = reason ?? purchase.FailureReason,
                TicketIds = ticketIds ?? purchase.TicketIds,
            };
            return Task.FromResult(true);
        }

        public Task<bool> TryNoteFailureAsync(string purchaseId, string reason, CancellationToken ct)
        {
            if (!Store.TryGetValue(purchaseId, out var purchase)
                || purchase.Status is not (PurchaseStatus.SEATS_RESERVED or PurchaseStatus.PAYMENT_APPROVED)
                || purchase.PendingFailureReason is not null)
                return Task.FromResult(false);
            Store[purchaseId] = purchase with { PendingFailureReason = reason };
            return Task.FromResult(true);
        }
    }

    private sealed class FakeOutbox : IOutbox
    {
        public List<(string Queue, string Type, string PurchaseId, object Payload)> Enqueued { get; } = [];

        public Task EnqueueAsync(string queueName, string type, string purchaseId, object payload, CancellationToken ct)
        {
            Enqueued.Add((queueName, type, purchaseId, payload));
            return Task.CompletedTask;
        }
    }

    private static SagaEnvelope Env<T>(string type, string purchaseId, T payload) =>
        new(type, purchaseId, JsonSerializer.SerializeToElement(payload, SagaJson.Options));

    private static (SagaProcessManager Manager, FakePurchaseRepository Purchases, FakeOutbox Outbox) NewManager()
    {
        var purchases = new FakePurchaseRepository();
        var outbox = new FakeOutbox();
        var manager = new SagaProcessManager(purchases, outbox, NullLogger<SagaProcessManager>.Instance);
        return (manager, purchases, outbox);
    }

    private static (SagaProcessManager Manager, FakePurchaseRepository Purchases, FakeOutbox Outbox) Started(
        string purchaseId, int seats = 1, string pan = "4111111111111111")
    {
        var (manager, purchases, outbox) = NewManager();
        purchases.Store[purchaseId] = Purchase.Create(purchaseId, "evt-test", seats, pan, "key-1",
            new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        return (manager, purchases, outbox);
    }

    [Fact]
    public async Task Happy_path_reaches_TICKETS_ISSUED_with_commands_in_sequence()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id, seats: 3);

        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 85.50m)), CT);
        var afterReserve = purchases.Store[id];
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, afterReserve.Status);
        var authorize = Assert.Single(outbox.Enqueued);
        Assert.Equal((SagaQueues.PaymentsCommands, SagaTypes.AuthorizePayment, id), (authorize.Queue, authorize.Type, authorize.PurchaseId));
        var command = Assert.IsType<AuthorizePaymentCommand>(authorize.Payload);
        Assert.Equal("4111111111111111", command.Pan);
        Assert.Equal(85.50m * 3, command.Amount); // monto = precio del evento × asientos

        await manager.HandleAsync(Env(SagaTypes.PaymentApproved, id, new PaymentApprovedEvent(256.50m)), CT);
        Assert.Equal(PurchaseStatus.PAYMENT_APPROVED, purchases.Store[id].Status);
        var issue = outbox.Enqueued[^1];
        Assert.Equal((SagaQueues.TicketingCommands, SagaTypes.IssueTickets, id), (issue.Queue, issue.Type, issue.PurchaseId));

        var ticketIds = new[] { "tkt-1", "tkt-2", "tkt-3" };
        await manager.HandleAsync(Env(SagaTypes.TicketsIssued, id, new TicketsIssuedEvent(ticketIds)), CT);
        var final = purchases.Store[id];
        Assert.Equal(PurchaseStatus.TICKETS_ISSUED, final.Status);
        Assert.Equal(ticketIds, final.TicketIds);
        Assert.Equal(2, outbox.Enqueued.Count); // uno por paso — sin emisiones extra
    }

    [Fact]
    public async Task Payment_declined_releases_seat_and_fails_with_reason()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id, seats: 2);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 100m)), CT);

        await manager.HandleAsync(
            Env(SagaTypes.PaymentDeclined, id, new PaymentDeclinedEvent("tarjeta rechazada (PAN terminada en 0002)")), CT);
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, purchases.Store[id].Status); // aún no FAILED: compensa primero
        var release = Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats);
        Assert.Equal(SagaQueues.CatalogCommands, release.Queue);
        var releaseCommand = Assert.IsType<ReleaseSeatsCommand>(release.Payload);
        Assert.Equal(("evt-test", 2), (releaseCommand.EventId, releaseCommand.Seats));

        await manager.HandleAsync(Env(SagaTypes.PaymentDeclined, id, new PaymentDeclinedEvent("duplicado")), CT);
        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats); // duplicado: NO re-emite

        await manager.HandleAsync(Env(SagaTypes.SeatsReleased, id, new SeatsReleasedEvent("evt-test")), CT);
        var failed = purchases.Store[id];
        Assert.Equal(PurchaseStatus.FAILED, failed.Status);
        Assert.Contains("0002", failed.FailureReason); // motivo visible (H-02 AC3)
    }

    [Fact]
    public async Task Seats_unavailable_fails_directly_without_compensation()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id);

        await manager.HandleAsync(
            Env(SagaTypes.SeatsUnavailable, id, new SeatsUnavailableEvent("evt-test", "sin disponibilidad")), CT);

        var failed = purchases.Store[id];
        Assert.Equal(PurchaseStatus.FAILED, failed.Status);
        Assert.Equal("sin disponibilidad", failed.FailureReason);
        Assert.Empty(outbox.Enqueued); // nada reservado ⇒ nada que compensar
    }

    [Fact]
    public async Task Out_of_order_events_are_ignored()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id);

        await manager.HandleAsync(Env(SagaTypes.TicketsIssued, id, new TicketsIssuedEvent(["tkt-x"])), CT);
        await manager.HandleAsync(Env(SagaTypes.PaymentApproved, id, new PaymentApprovedEvent(1m)), CT);
        await manager.HandleAsync(Env(SagaTypes.SeatsReleased, id, new SeatsReleasedEvent("evt-test")), CT);

        Assert.Equal(PurchaseStatus.PENDING, purchases.Store[id].Status); // nada avanzó
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task Duplicate_seats_reserved_does_not_reenqueue_authorize_payment()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id);
        var reserved = Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 50m));

        await manager.HandleAsync(reserved, CT);
        await manager.HandleAsync(reserved, CT); // duplicado (redelivery)

        Assert.Single(outbox.Enqueued); // un solo authorize-payment (hook H-03 AC2)
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, purchases.Store[id].Status);
    }

    // --- D-0012 (N-2002 AC2/AC3): evento step-failed (reintentos agotados) ------------

    [Fact]
    public async Task Step_failed_on_PENDING_fails_directly_without_compensation()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id); // sigue PENDING: falló ANTES de reservar

        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("reserve-seats", "el paso 'reserve-seats' falló tras 3 intentos (InvalidOperationException)")), CT);

        var failed = purchases.Store[id];
        Assert.Equal(PurchaseStatus.FAILED, failed.Status); // terminal explícito con motivo (AC2)
        Assert.Contains("reserve-seats", failed.FailureReason);
        Assert.Empty(outbox.Enqueued); // sin reserva ⇒ sin compensación
    }

    [Fact]
    public async Task Step_failed_on_SEATS_RESERVED_compensates_and_fails_on_seats_released()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id, seats: 2);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 100m)), CT);

        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("authorize-payment", "el paso 'authorize-payment' falló tras 3 intentos (AmazonServiceException)")), CT);

        var release = Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats);
        var command = Assert.IsType<ReleaseSeatsCommand>(release.Payload);
        Assert.Equal(("evt-test", 2), (command.EventId, command.Seats)); // compensación reutilizada (D-0012)
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, purchases.Store[id].Status); // espera al seats-released

        await manager.HandleAsync(Env(SagaTypes.SeatsReleased, id, new SeatsReleasedEvent("evt-test")), CT);
        var failed = purchases.Store[id];
        Assert.Equal(PurchaseStatus.FAILED, failed.Status);
        Assert.Contains("authorize-payment", failed.FailureReason); // el motivo del paso que falló
    }

    [Fact]
    public async Task Step_failed_on_PAYMENT_APPROVED_compensates_and_fails_on_seats_released()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id, seats: 1);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 70m)), CT);
        await manager.HandleAsync(Env(SagaTypes.PaymentApproved, id, new PaymentApprovedEvent(70m)), CT);

        // issue-tickets agota reintentos con el pago ya autorizado: igual libera el asiento.
        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("issue-tickets", "el paso 'issue-tickets' falló tras 3 intentos (AmazonServiceException)")), CT);

        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats);
        Assert.Equal(PurchaseStatus.PAYMENT_APPROVED, purchases.Store[id].Status);

        await manager.HandleAsync(Env(SagaTypes.SeatsReleased, id, new SeatsReleasedEvent("evt-test")), CT);
        Assert.Equal(PurchaseStatus.FAILED, purchases.Store[id].Status); // terminal también desde PA
    }

    [Fact]
    public async Task Duplicate_step_failed_is_ignored_without_double_release()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 90m)), CT);
        var stepFailed = Env(SagaTypes.StepFailed, id, new StepFailedEvent("authorize-payment", "falló"));

        await manager.HandleAsync(stepFailed, CT);
        await manager.HandleAsync(stepFailed, CT); // duplicado (redelivery)

        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats); // una sola compensación
    }

    // --- F-V1-01 (enmienda D-0012): la compensación misma agota sus reintentos ----------

    [Fact]
    public async Task Step_failed_on_release_seats_goes_terminal_FAILED_declaring_orphan_seat()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id, seats: 2);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 100m)), CT);
        await manager.HandleAsync(
            Env(SagaTypes.PaymentDeclined, id, new PaymentDeclinedEvent("tarjeta rechazada (PAN terminada en 0002)")), CT);
        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats);

        // Repro QA F-V1-01: la compensación también agota 3+3 intentos → 2do step-failed.
        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("release-seats", "el paso 'release-seats' falló tras 3 intentos (InjectedFailureException)")), CT);

        var failed = purchases.Store[id];
        Assert.Equal(PurchaseStatus.FAILED, failed.Status); // terminal: JAMÁS SEATS_RESERVED eterno (AC2)
        Assert.Contains("0002", failed.FailureReason); // el motivo original viaja
        Assert.Contains("release-seats", failed.FailureReason); // y también el fallo de la compensación
        Assert.Contains("huérfano", failed.FailureReason); // la reserva huérfana queda declarada (AC3)
        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats); // no re-emite infinito
    }

    [Fact]
    public async Task Step_failed_on_release_seats_after_issue_tickets_also_goes_terminal()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id, seats: 1);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 70m)), CT);
        await manager.HandleAsync(Env(SagaTypes.PaymentApproved, id, new PaymentApprovedEvent(70m)), CT);
        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("issue-tickets", "el paso 'issue-tickets' falló tras 3 intentos")), CT);

        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("release-seats", "el paso 'release-seats' falló tras 3 intentos")), CT);

        var failed = purchases.Store[id];
        Assert.Equal(PurchaseStatus.FAILED, failed.Status); // terminal también desde PAYMENT_APPROVED
        Assert.Contains("issue-tickets", failed.FailureReason);
        Assert.Contains("huérfano", failed.FailureReason);
        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats);
    }

    [Fact]
    public async Task Duplicate_step_failed_on_release_seats_after_terminal_is_ignored()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = Started(id);
        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 90m)), CT);
        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent("authorize-payment", "falló")), CT);
        var compensationFailed = Env(SagaTypes.StepFailed, id,
            new StepFailedEvent("release-seats", "el paso 'release-seats' falló tras 3 intentos"));

        await manager.HandleAsync(compensationFailed, CT); // → FAILED terminal
        await manager.HandleAsync(compensationFailed, CT); // duplicado (redelivery)

        Assert.Equal(PurchaseStatus.FAILED, purchases.Store[id].Status); // sin mutar
        Assert.Single(outbox.Enqueued, e => e.Type == SagaTypes.ReleaseSeats); // sin re-emisión
    }

    private static CancellationToken CT => CancellationToken.None;
}
