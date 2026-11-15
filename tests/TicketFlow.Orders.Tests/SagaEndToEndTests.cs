using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SQS;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;
using TicketFlow.Catalog.Core.Infrastructure;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Payments.Core.Application;
using TicketFlow.Payments.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;
using TicketFlow.Ticketing.Core.Application;
using TicketFlow.Ticketing.Core.Infrastructure;
using Amazon.SQS.Model;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Saga completa contra el emulador REAL (D-0006): colas SQS FIFO reales de ida y vuelta,
/// DynamoDB real (compras, outbox, pagos, tickets, catálogo), S3 real (payloads QR). Los workers
/// se manejan a nivel componente (receive → handler → delete, igual que SqsSagaWorker) para no
/// requerir 4 procesos host — la corrida con procesos reales es la evidencia funcional de S02.
/// </summary>
public sealed class SagaEndToEndTests
{
    private sealed class Harness
    {
        public EmulatorAwsOptions Options { get; } = EmulatorAwsOptions.FromEnvironment();
        public IAmazonSQS Sqs { get; } = EmulatorAwsClients.CreateSqs(EmulatorAwsOptions.FromEnvironment());
        public SqsSagaTransport Transport { get; }
        public DynamoDbEventRepository CatalogRepo { get; }
        public AvailabilityService Availability { get; }
        public DynamoDbPurchaseRepository Purchases { get; }
        public DynamoDbOutbox Outbox { get; }
        public SagaProcessManager ProcessManager { get; }
        public DynamoDbPaymentRepository Payments { get; }
        public PaymentsSagaHandler PaymentsHandler { get; }
        public DynamoDbTicketRepository Tickets { get; }
        public S3QrPayloadStore QrStore { get; }
        public TicketingSagaHandler TicketingHandler { get; }
        public CatalogSagaHandler CatalogHandler { get; }

        public Harness()
        {
            var ddb = EmulatorAwsClients.CreateDynamoDb(Options);
            var s3 = EmulatorAwsClients.CreateS3(Options);
            Transport = new SqsSagaTransport(Sqs);
            CatalogRepo = new DynamoDbEventRepository(ddb);
            var services = new ServiceCollection();
            services.AddHybridCache();
            Availability = new AvailabilityService(
                CatalogRepo,
                services.BuildServiceProvider().GetRequiredService<HybridCache>(),
                new AvailabilityCacheOptions(), NullLogger<AvailabilityService>.Instance);
            CatalogHandler = new CatalogSagaHandler(CatalogRepo, Availability, NullLogger<CatalogSagaHandler>.Instance);
            Purchases = new DynamoDbPurchaseRepository(ddb);
            Outbox = new DynamoDbOutbox(ddb);
            ProcessManager = new SagaProcessManager(Purchases, Outbox, NullLogger<SagaProcessManager>.Instance);
            Payments = new DynamoDbPaymentRepository(ddb);
            PaymentsHandler = new PaymentsSagaHandler(Payments, NullLogger<PaymentsSagaHandler>.Instance);
            Tickets = new DynamoDbTicketRepository(ddb);
            QrStore = new S3QrPayloadStore(s3);
            TicketingHandler = new TicketingSagaHandler(Tickets, QrStore, NullLogger<TicketingSagaHandler>.Instance);
        }
    }

    private static string NewEventId() => "it-" + Guid.NewGuid().ToString("N")[..12];

    private delegate Task<IReadOnlyList<SagaEnvelope>> Handler(SagaEnvelope envelope, CancellationToken ct);

    /// <summary>Una pasada de receive→handler→publish→delete sobre una cola (igual que el worker real).</summary>
    private static async Task DrainAsync(Harness h, string queueName, Handler handler, CancellationToken ct)
    {
        var url = await h.Transport.EnsureQueueAsync(queueName, ct);
        var received = await h.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = url,
            MaxNumberOfMessages = 10,
            WaitTimeSeconds = 0,
        }, ct);
        if (received.Messages is not { Count: > 0 }) return; // el emulador da Messages=null si está vacía
        foreach (var message in received.Messages)
        {
            var envelope = SagaJson.Deserialize(message.Body);
            foreach (var @event in await handler(envelope, ct))
                await h.Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
            await h.Sqs.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = url,
                ReceiptHandle = message.ReceiptHandle,
            }, ct);
        }
    }

    /// <summary>Bombea la saga (relay + 4 colas) hasta estado terminal o timeout explícito.</summary>
    private static async Task<Purchase> PumpUntilTerminalAsync(Harness h, string purchaseId, CancellationToken ct)
    {
        Handler catalog = (e, token) => h.CatalogHandler.HandleAsync(e, token);
        Handler payments = async (e, token) => await h.PaymentsHandler.HandleAsync(e, token) is { } ev
            ? [ev] : [];
        Handler ticketing = async (e, token) => await h.TicketingHandler.HandleAsync(e, token) is { } ev
            ? [ev] : [];
        Handler orders = async (e, token) =>
        {
            await h.ProcessManager.HandleAsync(e, token);
            return [];
        };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await h.Outbox.PublishPendingAsync(h.Transport, NullLogger<DynamoDbOutbox>.Instance, ct);
            await DrainAsync(h, SagaQueues.CatalogCommands, catalog, ct);
            await DrainAsync(h, SagaQueues.PaymentsCommands, payments, ct);
            await DrainAsync(h, SagaQueues.TicketingCommands, ticketing, ct);
            await DrainAsync(h, SagaQueues.OrdersEvents, orders, ct);

            var purchase = await h.Purchases.GetAsync(purchaseId, ct);
            if (purchase?.Status is PurchaseStatus.TICKETS_ISSUED or PurchaseStatus.FAILED)
                return purchase;
            await Task.Delay(100, ct);
        }

        // Diagnóstico del estancamiento: estado de la compra + mensajes visibles por cola.
        var stuck = await h.Purchases.GetAsync(purchaseId, ct);
        var depths = new List<string>();
        foreach (var queue in SagaQueues.All)
        {
            var url = await h.Transport.EnsureQueueAsync(queue, ct);
            var attrs = await h.Sqs.GetQueueAttributesAsync(new Amazon.SQS.Model.GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"],
            }, ct);
            depths.Add($"{queue}: visible={attrs.ApproximateNumberOfMessages} in-flight={attrs.ApproximateNumberOfMessagesNotVisible}");
        }
        throw new TimeoutException(
            $"La compra {purchaseId} no llegó a estado terminal en 60 s. Estado: {stuck?.Status}; " +
            $"pending_failure_reason: {stuck?.PendingFailureReason ?? "-"}; colas: {string.Join(" | ", depths)}");
    }

    private static async Task<(Harness Harness, string EventId, int Initial)> SeededEventAsync(
        int available, decimal price, CancellationToken ct)
    {
        var harness = new Harness();
        var eventId = NewEventId();
        await harness.CatalogRepo.InitializeAsync(
            [CatalogEvent.Create(eventId, "E2E Show",
                new DateTimeOffset(2027, 5, 1, 20, 0, 0, TimeSpan.Zero), price, available)], ct);
        return (harness, eventId, available);
    }

    private static SagaEnvelope Envelope<T>(string type, string purchaseId, T payload) =>
        new(type, purchaseId, JsonSerializer.SerializeToElement(payload, SagaJson.Options));

    /// <summary>
    /// Worker REAL del Catálogo (mismo cuerpo que CatalogSagaWorker de producción: handler +
    /// publicación a OrdersEvents) con <see cref="FailureInjector"/> — para el repro F-V1-01.
    /// </summary>
    private sealed class InjectedCatalogWorker(
        IAmazonSQS sqs, SqsSagaTransport transport, CatalogSagaHandler handler, FailureInjector injector)
        : SqsSagaWorker(sqs, transport, SagaQueues.CatalogCommands, NullLogger.Instance, injector)
    {
        protected override async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
        {
            foreach (var @event in await handler.HandleAsync(envelope, ct))
                await Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
        }
    }

    /// <summary>
    /// Worker REAL con injector sobre cualquier cola (repros de doble fallo F-V1-R2-01):
    /// handler real del servicio + publicación de su evento a OrdersEvents, igual que los
    /// workers de producción — el agotamiento emite step-failed por el transporte real.
    /// </summary>
    private sealed class InjectedWorker(
        IAmazonSQS sqs, SqsSagaTransport transport, string queueName, Handler handler, FailureInjector injector)
        : SqsSagaWorker(sqs, transport, queueName, NullLogger.Instance, injector)
    {
        protected override async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
        {
            foreach (var @event in await handler(envelope, ct))
                await Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
        }
    }

    /// <summary>
    /// Bombea la saga (relay + colas manuales) hasta estado terminal, saltando las colas que
    /// consumen workers reales con inyección (F-V1-R2-01: cada step-failed de un paso fallado
    /// debe llegar a Órdenes — el dedup id del transporte ahora incluye el paso).
    /// </summary>
    private static async Task<Purchase> PumpUntilTerminalExceptAsync(
        Harness h, string purchaseId, CancellationToken ct, params string[] realWorkerQueues)
    {
        Handler catalog = (e, token) => h.CatalogHandler.HandleAsync(e, token);
        Handler payments = async (e, token) => await h.PaymentsHandler.HandleAsync(e, token) is { } ev
            ? [ev] : [];
        Handler ticketing = async (e, token) => await h.TicketingHandler.HandleAsync(e, token) is { } ev
            ? [ev] : [];
        Handler orders = async (e, token) =>
        {
            await h.ProcessManager.HandleAsync(e, token);
            return [];
        };

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await h.Outbox.PublishPendingAsync(h.Transport, NullLogger<DynamoDbOutbox>.Instance, ct);
            if (!realWorkerQueues.Contains(SagaQueues.CatalogCommands))
                await DrainAsync(h, SagaQueues.CatalogCommands, catalog, ct);
            if (!realWorkerQueues.Contains(SagaQueues.PaymentsCommands))
                await DrainAsync(h, SagaQueues.PaymentsCommands, payments, ct);
            if (!realWorkerQueues.Contains(SagaQueues.TicketingCommands))
                await DrainAsync(h, SagaQueues.TicketingCommands, ticketing, ct);
            await DrainAsync(h, SagaQueues.OrdersEvents, orders, ct);

            var purchase = await h.Purchases.GetAsync(purchaseId, ct);
            if (purchase?.Status is PurchaseStatus.TICKETS_ISSUED or PurchaseStatus.FAILED)
                return purchase;
            await Task.Delay(100, ct);
        }

        var stuck = await h.Purchases.GetAsync(purchaseId, ct);
        throw new TimeoutException(
            $"La compra {purchaseId} no llegó a estado terminal en 60 s (F-V1-R2-01). Estado: " +
            $"{stuck?.Status}; pending_failure_reason: {stuck?.PendingFailureReason ?? "-"}");
    }

    [SkippableFact]
    public async Task Approved_purchase_reaches_TICKETS_ISSUED_with_ticket_qr_and_exact_decrement()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (h, eventId, initial) = await SeededEventAsync(10, 120.50m, ct);
        await h.Payments.InitializeAsync(ct);
        await h.Tickets.InitializeAsync(ct);
        await h.QrStore.InitializeAsync(ct);

        var cachedBefore = await h.Availability.GetAvailabilityAsync(eventId, ct);
        Assert.False(cachedBefore.FromCache);

        var purchase = Purchase.Create(PurchaseIds.New(), eventId, 1, "4111111111111111",
            "e2e-key-1", DateTimeOffset.UtcNow);
        await h.Purchases.CreateAsync(purchase, ct);
        await h.Outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats, purchase.PurchaseId,
            new ReserveSeatsCommand(eventId, 1), ct);

        var final = await PumpUntilTerminalAsync(h, purchase.PurchaseId, ct);

        // H-02 AC1: terminal TICKETS_ISSUED con ticket referenciable.
        Assert.Equal(PurchaseStatus.TICKETS_ISSUED, final.Status);
        var ticketId = Assert.Single(final.TicketIds!);
        var ticket = await h.Tickets.GetAsync(ticketId, ct);
        Assert.NotNull(ticket);
        Assert.Equal(purchase.PurchaseId, ticket.PurchaseId);
        Assert.Equal(eventId, ticket.EventId);
        var qr = await h.QrStore.GetAsync(ticket.QrS3Key, ct);
        Assert.NotNull(qr);
        Assert.Contains(ticketId, qr); // el payload QR referencia la entrada

        // H-02 AC2: disponibilidad −1 exacto.
        var stored = await h.CatalogRepo.GetByIdAsync(eventId, ct);
        Assert.Equal(initial - 1, stored!.Available);
        // H-01 AC2 (cierre): el cache quedó invalidado por la compra — la lectura vuelve al almacén.
        var after = await h.Availability.GetAvailabilityAsync(eventId, ct);
        Assert.False(after.FromCache);
        Assert.Equal(initial - 1, after.Available);

        // Evidencia del paso de pago (plan §2).
        var payment = await h.Payments.GetAsync(purchase.PurchaseId, ct);
        Assert.NotNull(payment);
        Assert.True(payment.Approved);
        Assert.Equal(120.50m, payment.Amount);
    }

    [SkippableFact]
    public async Task Declined_pan_fails_with_reason_and_restores_availability_exactly()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (h, eventId, initial) = await SeededEventAsync(10, 85.00m, ct);
        await h.Payments.InitializeAsync(ct);
        await h.Tickets.InitializeAsync(ct);
        await h.QrStore.InitializeAsync(ct);

        var purchase = Purchase.Create(PurchaseIds.New(), eventId, 2, "5105105105100002",
            "e2e-key-2", DateTimeOffset.UtcNow);
        await h.Purchases.CreateAsync(purchase, ct);
        await h.Outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats, purchase.PurchaseId,
            new ReserveSeatsCommand(eventId, 2), ct);

        var final = await PumpUntilTerminalAsync(h, purchase.PurchaseId, ct);

        // H-02 AC3: FAILED con motivo visible y disponibilidad restaurada (compensación observable).
        Assert.Equal(PurchaseStatus.FAILED, final.Status);
        Assert.NotNull(final.FailureReason);
        Assert.Contains("0002", final.FailureReason);
        var restored = await h.CatalogRepo.GetByIdAsync(eventId, ct);
        Assert.Equal(initial, restored!.Available); // 10 → 8 (reserva) → 10 (release): exacto

        // El pago rechazado quedó como evidencia y no se emitieron entradas.
        var payment = await h.Payments.GetAsync(purchase.PurchaseId, ct);
        Assert.NotNull(payment);
        Assert.False(payment.Approved);
        Assert.Null(await h.Tickets.GetIssuedTicketIdsAsync(purchase.PurchaseId, ct));
    }

    // F-V1-01 (enmienda D-0012, repro del QA): persistent en release-seats — la compensación
    // misma agota 3+3 intentos. La compra DEBE terminar FAILED con la reserva huérfana
    // declarada (nada en SEATS_RESERVED para siempre) y la entrega inspeccionable en la DLQ.
    [SkippableFact]
    public async Task Persistent_failure_on_release_seats_ends_terminal_failed_with_explicit_orphan()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (h, eventId, initial) = await SeededEventAsync(6, 90.00m, ct);
        await h.Payments.InitializeAsync(ct);

        var injector = new FailureInjector();
        injector.Inject(SagaTypes.ReleaseSeats, FailureInjector.Persistent, 2);
        var catalogWorker = new InjectedCatalogWorker(h.Sqs, h.Transport, h.CatalogHandler, injector);
        await catalogWorker.StartAsync(ct);
        try
        {
            // PAN 0002 → payment-declined → compensación release-seats → 3 fallos → step-failed.
            var purchase = Purchase.Create(PurchaseIds.New(), eventId, 2, "5105105105100002",
                "e2e-key-fv1", DateTimeOffset.UtcNow);
            await h.Purchases.CreateAsync(purchase, ct);
            await h.Outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats,
                purchase.PurchaseId, new ReserveSeatsCommand(eventId, 2), ct);

            // Bombea TODO menos catalog-commands (esa cola es del worker real): hasta terminal.
            Handler payments = async (e, token) => await h.PaymentsHandler.HandleAsync(e, token) is { } ev
                ? [ev] : [];
            Handler orders = async (e, token) =>
            {
                await h.ProcessManager.HandleAsync(e, token);
                return [];
            };
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            Purchase? final = null;
            while (final?.Status is not (PurchaseStatus.TICKETS_ISSUED or PurchaseStatus.FAILED)
                   && DateTimeOffset.UtcNow < deadline)
            {
                await h.Outbox.PublishPendingAsync(h.Transport, NullLogger<DynamoDbOutbox>.Instance, ct);
                await DrainAsync(h, SagaQueues.PaymentsCommands, payments, ct);
                await DrainAsync(h, SagaQueues.OrdersEvents, orders, ct);
                final = await h.Purchases.GetAsync(purchase.PurchaseId, ct);
                await Task.Delay(100, ct);
            }

            // AC2: terminal explícito con motivo — el original Y el fallo de la compensación.
            Assert.NotNull(final);
            Assert.Equal(PurchaseStatus.FAILED, final.Status);
            Assert.Contains("0002", final.FailureReason);
            Assert.Contains("release-seats", final.FailureReason);
            Assert.Contains("huérfano", final.FailureReason);

            // AC3 sin drift OCULTO: la disponibilidad refleja exactamente la reserva huérfana.
            var stored = await h.CatalogRepo.GetByIdAsync(eventId, ct);
            Assert.Equal(initial - 2, stored!.Available);

            // DLQ inspeccionable (AC2, restaurado — F-V1-R3-01): la enumeración completa del
            // peek hace el assert determinista aun con deuda acumulada en la DLQ compartida.
            var catalogDlq = await h.Transport.PeekDlqAsync(SagaQueues.CatalogCommands, ct);
            Assert.Contains(catalogDlq, e => e.PurchaseId == purchase.PurchaseId);
        }
        finally
        {
            await catalogWorker.StopAsync(CancellationToken.None);
        }
    }

    // F-V1-R2-01 (repro QA a): issue-tickets + release-seats persistent con PAN APROBADO —
    // DOS step-failed de la MISMA compra (pasos distintos). Con el dedup id {type}:{purchase}
    // SQS FIFO tragaba el segundo y la compra quedaba PAYMENT_APPROVED eterna. DEBE terminar
    // FAILED con la reserva huérfana declarada y disponibilidad explícita sin drift oculto.
    [SkippableFact]
    public async Task Double_failure_issue_tickets_and_release_seats_ends_terminal_failed_with_orphan()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (h, eventId, initial) = await SeededEventAsync(5, 100.00m, ct);
        await h.Payments.InitializeAsync(ct);

        var catalogInjector = new FailureInjector();
        catalogInjector.Inject(SagaTypes.ReleaseSeats, FailureInjector.Persistent, 2);
        var ticketingInjector = new FailureInjector();
        ticketingInjector.Inject(SagaTypes.IssueTickets, FailureInjector.Persistent, 2);

        Handler catalog = (e, token) => h.CatalogHandler.HandleAsync(e, token);
        Handler ticketing = async (e, token) => await h.TicketingHandler.HandleAsync(e, token) is { } ev
            ? [ev] : [];

        var catalogWorker = new InjectedWorker(h.Sqs, h.Transport, SagaQueues.CatalogCommands,
            catalog, catalogInjector);
        var ticketingWorker = new InjectedWorker(h.Sqs, h.Transport, SagaQueues.TicketingCommands,
            ticketing, ticketingInjector);
        await catalogWorker.StartAsync(ct);
        await ticketingWorker.StartAsync(ct);
        try
        {
            // PAN aprobado: reserva OK → pago OK → issue-tickets agota 3 intentos (1er
            // step-failed) → compensación release-seats agota 3 intentos (2do step-failed).
            var purchase = Purchase.Create(PurchaseIds.New(), eventId, 1, "4111111111111111",
                "e2e-key-r2a", DateTimeOffset.UtcNow);
            await h.Purchases.CreateAsync(purchase, ct);
            await h.Outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats,
                purchase.PurchaseId, new ReserveSeatsCommand(eventId, 1), ct);

            var final = await PumpUntilTerminalExceptAsync(h, purchase.PurchaseId, ct,
                SagaQueues.CatalogCommands, SagaQueues.TicketingCommands);

            // AC2: terminal explícito — fallo original anotado + compensación agotada declarada.
            Assert.Equal(PurchaseStatus.FAILED, final.Status);
            Assert.NotNull(final.FailureReason);
            Assert.Contains(SagaTypes.IssueTickets, final.FailureReason);
            Assert.Contains(SagaTypes.ReleaseSeats, final.FailureReason);
            Assert.Contains("huérfano", final.FailureReason);

            // AC3 sin drift oculto: disponibilidad explícita = asiento tomado (huérfano).
            var stored = await h.CatalogRepo.GetByIdAsync(eventId, ct);
            Assert.Equal(initial - 1, stored!.Available);

            // DLQ inspeccionable (restaurado, F-V1-R3-01): AMBAS entregas fallidas (paso +
            // compensación) están en sus DLQs — la enumeración completa tolera la deuda de
            // corridas previas. Lo esencial de F-V1-R2-01 sigue asertado arriba: el 2do
            // step-failed LLEGÓ (terminal + reason) — antes lo tragaba el dedup.
            var ticketingDlq = await h.Transport.PeekDlqAsync(SagaQueues.TicketingCommands, ct);
            Assert.Contains(ticketingDlq, e => e.PurchaseId == purchase.PurchaseId);
            Assert.Contains(await h.Transport.PeekDlqAsync(SagaQueues.CatalogCommands, ct),
                e => e.PurchaseId == purchase.PurchaseId);
        }
        finally
        {
            await catalogWorker.StopAsync(CancellationToken.None);
            await ticketingWorker.StopAsync(CancellationToken.None);
        }
    }

    // F-V1-R2-01 (repro QA b): authorize-payment + release-seats persistent con PAN APROBADO —
    // fallo temprano que compensa y compensación que falla: 2 step-failed (pasos distintos)
    // de la misma compra. Con el dedup colisionado quedaba SEATS_RESERVED eterna sin reason
    // (síntoma de F-V1-01 con 2 reglas). DEBE terminar FAILED con la huérfana declarada.
    [SkippableFact]
    public async Task Double_failure_authorize_payment_and_release_seats_ends_terminal_failed_with_orphan()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (h, eventId, initial) = await SeededEventAsync(4, 75.00m, ct);
        await h.Payments.InitializeAsync(ct);

        var catalogInjector = new FailureInjector();
        catalogInjector.Inject(SagaTypes.ReleaseSeats, FailureInjector.Persistent, 2);
        var paymentsInjector = new FailureInjector();
        paymentsInjector.Inject(SagaTypes.AuthorizePayment, FailureInjector.Persistent, 2);

        Handler catalog = (e, token) => h.CatalogHandler.HandleAsync(e, token);
        Handler payments = async (e, token) => await h.PaymentsHandler.HandleAsync(e, token) is { } ev
            ? [ev] : [];

        var catalogWorker = new InjectedWorker(h.Sqs, h.Transport, SagaQueues.CatalogCommands,
            catalog, catalogInjector);
        var paymentsWorker = new InjectedWorker(h.Sqs, h.Transport, SagaQueues.PaymentsCommands,
            payments, paymentsInjector);
        await catalogWorker.StartAsync(ct);
        await paymentsWorker.StartAsync(ct);
        try
        {
            var purchase = Purchase.Create(PurchaseIds.New(), eventId, 2, "4111111111111111",
                "e2e-key-r2b", DateTimeOffset.UtcNow);
            await h.Purchases.CreateAsync(purchase, ct);
            await h.Outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats,
                purchase.PurchaseId, new ReserveSeatsCommand(eventId, 2), ct);

            var final = await PumpUntilTerminalExceptAsync(h, purchase.PurchaseId, ct,
                SagaQueues.CatalogCommands, SagaQueues.PaymentsCommands);

            Assert.Equal(PurchaseStatus.FAILED, final.Status);
            Assert.NotNull(final.FailureReason);
            Assert.Contains(SagaTypes.AuthorizePayment, final.FailureReason);
            Assert.Contains(SagaTypes.ReleaseSeats, final.FailureReason);
            Assert.Contains("huérfano", final.FailureReason);

            var stored = await h.CatalogRepo.GetByIdAsync(eventId, ct);
            Assert.Equal(initial - 2, stored!.Available);

            // Sin assert de contenido DLQ (misma razón que el repro a: peek de 10 sobre una
            // DLQ compartida que solo acumula). Lo esencial: el step-failed de la compensación
            // llegó a Órdenes pese al previo de la misma compra → FAILED + huérfana declarada.
        }
        finally
        {
            await catalogWorker.StopAsync(CancellationToken.None);
            await paymentsWorker.StopAsync(CancellationToken.None);
        }
    }
}
