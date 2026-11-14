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
}
