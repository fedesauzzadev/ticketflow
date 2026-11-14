using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;
using TicketFlow.Catalog.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Dedup de comandos internos (H-03 AC2, D-0009) contra emulador real: la capa transporte
/// (SQS FIFO ContentBasedDeduplication) traga el reenvío idéntico, y cuando el duplicado
/// PASA (dedup id distinto, correlación igual — p.ej. reenvío fuera de ventana) la capa de
/// aplicación (claim RESERVATION#) garantiza un solo decremento con LOG observable.
/// </summary>
public sealed class CatalogSagaDedupTests
{
    /// <summary>Logger de captura para asertar el "duplicado detectado: efecto descartado" (AC2).</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }
    }

    private sealed class Harness
    {
        public DynamoDbEventRepository Repo { get; }
        public AvailabilityService Availability { get; }
        public SqsSagaTransport Transport { get; }
        public IAmazonSQS Sqs { get; }
        public RecordingLogger<CatalogSagaHandler> Logger { get; } = new();
        public CatalogSagaHandler Handler { get; }

        public Harness()
        {
            var options = EmulatorAwsOptions.FromEnvironment();
            Sqs = EmulatorAwsClients.CreateSqs(options);
            Transport = new SqsSagaTransport(Sqs);
            Repo = new DynamoDbEventRepository(EmulatorAwsClients.CreateDynamoDb(options));
            var services = new ServiceCollection();
            services.AddHybridCache();
            Availability = new AvailabilityService(Repo,
                services.BuildServiceProvider().GetRequiredService<HybridCache>(),
                new AvailabilityCacheOptions(), NullLogger<AvailabilityService>.Instance);
            Handler = new CatalogSagaHandler(Repo, Availability, Logger);
        }
    }

    private static SagaEnvelope Envelope<T>(string type, string purchaseId, T payload) =>
        new(type, purchaseId, JsonSerializer.SerializeToElement(payload, SagaJson.Options));

    /// <summary>Recibe y procesa los mensajes de la cola que matchean purchaseId (ignora y no
    /// borra los de otras corridas paralelas). Devuelve cuántos comandos de esa compra llegaron.</summary>
    private static async Task<int> DrainAndHandleAsync(Harness h, string queueUrl, string purchaseId,
        CancellationToken ct)
    {
        var handled = 0;
        for (var poll = 0; poll < 15; poll++)
        {
            var received = await h.Sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
            }, ct);
            if (received.Messages is not { Count: > 0 }) break; // como L-005: Messages=null si vacía

            var any = false;
            foreach (var message in received.Messages)
            {
                if (!message.Body.Contains(purchaseId)) continue; // mensaje de otra corrida
                any = true;
                await h.Handler.HandleAsync(SagaJson.Deserialize(message.Body), ct);
                handled++;
                await h.Sqs.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle,
                }, ct);
            }
            if (!any) break; // la ronda no trajo nada nuestro: fin
        }
        return handled;
    }

    [SkippableFact]
    public async Task Duplicated_reserve_command_decrements_once_across_both_dedup_layers_with_log()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var h = new Harness();
        var eventId = "it-" + Guid.NewGuid().ToString("N")[..12];
        await h.Repo.InitializeAsync([CatalogEvent.Create(eventId, "Dedup Show",
            new DateTimeOffset(2027, 8, 1, 20, 0, 0, TimeSpan.Zero), 60m, 5)], ct);

        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        var envelope = Envelope(SagaTypes.ReserveSeats, purchaseId, new ReserveSeatsCommand(eventId, 2));
        // Cola dedicada por corrida: la compartida la consumen también los workers de los hosts
        // de otras clases de test (paralelas) y se roban los mensajes de este test (L-007).
        // Mismos atributos y mismo código de transporte: las dos capas de dedup quedan probadas.
        var queueName = $"it-catalog-dedup-{Guid.NewGuid().ToString("N")[..12]}.fifo";
        var queueUrl = await h.Transport.EnsureQueueAsync(queueName, ct);

        // 1) Capa transporte: el mismo comando encolado 2 VECES con igual dedup id de SQS FIFO
        //    → ContentBasedDeduplication deja pasar UNO solo.
        await h.Transport.SendAsync(queueName, envelope, ct);
        await h.Transport.SendAsync(queueName, envelope, ct);
        var transportDelivered = await DrainAndHandleAsync(h, queueUrl, purchaseId, ct);
        Assert.Equal(1, transportDelivered);

        // 2) Capa aplicación: reenvío con dedup id DISTINTO (correlación igual — como un relay
        //    que re-publica fuera de la ventana de dedup) → SQS lo entrega, el handler descarta.
        await h.Sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = SagaJson.SerializeEnvelope(envelope),
            MessageGroupId = purchaseId,
            MessageDeduplicationId = $"{SagaTypes.ReserveSeats}:{purchaseId}:beyond-window",
        }, ct);        var appDelivered = await DrainAndHandleAsync(h, queueUrl, purchaseId, ct);
        Assert.Equal(1, appDelivered);

        // Efecto total: UN solo decremento (5 → 3), aunque el comando se procesó 2 veces.
        Assert.Equal(3, (await h.Repo.GetByIdAsync(eventId, ct))!.Available);

        // Log observable del duplicado descartado por la capa de aplicación (H-03 AC2).
        Assert.Contains(h.Logger.Lines,
            line => line.Contains("reserve-seats") && line.Contains(purchaseId)
                    && line.Contains("duplicado detectado: efecto descartado"));
    }
}
