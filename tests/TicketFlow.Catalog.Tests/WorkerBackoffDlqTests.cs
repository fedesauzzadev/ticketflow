using Amazon.SQS;
using Microsoft.Extensions.Logging;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Backoff + DLQ aplicativa del worker base (D-0012, N-2002 AC1/AC2) contra el emulador real:
/// el SqsSagaWorker de producción (receive → backoff → handler → delete/DLQ) consumiendo una
/// cola dedicada por corrida (L-008). Transient count=2 = AC1 exacto: 2 reintentos con esperas
/// crecientes visibles en timestamps. Persistent: agotados 3 intentos → mensaje en la DLQ
/// inspeccionable con PeekDlqAsync (lo que GET /admin/dlq expone).
/// </summary>
public sealed class WorkerBackoffDlqTests
{
    private sealed class RecordingLogger : ILogger
    {
        public sealed record Line(DateTimeOffset At, string Message);

        public List<Line> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(new Line(DateTimeOffset.UtcNow, formatter(state, exception)));
        }
    }

    /// <summary>
    /// Worker de prueba sobre la base REAL: cuenta éxitos, no emite step-failed (un test no
    /// alimenta la cola compartida de Órdenes — L-008).
    /// </summary>
    private sealed class TestWorker(
        IAmazonSQS sqs, SqsSagaTransport transport, string queueName, RecordingLogger logger,
        FailureInjector injector) : SqsSagaWorker(sqs, transport, queueName, logger, injector)
    {
        public int Successes { get; private set; }
        public DateTimeOffset? LastSuccessAt { get; private set; }

        protected override bool EmitsStepFailed => false;

        protected override Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
        {
            Successes++;
            LastSuccessAt = DateTimeOffset.UtcNow;
            return Task.CompletedTask;
        }
    }

    private static async Task<(TestWorker Worker, RecordingLogger Logger)> StartWorkerAsync(
        string queueName, FailureInjector injector, CancellationToken ct)
    {
        var options = EmulatorAwsOptions.FromEnvironment();
        var sqs = EmulatorAwsClients.CreateSqs(options);
        var transport = new SqsSagaTransport(sqs);
        var logger = new RecordingLogger();
        var worker = new TestWorker(sqs, transport, queueName, logger, injector);
        await worker.StartAsync(ct);
        return (worker, logger);
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, TimeSpan deadline, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!await condition())
        {
            if (sw.Elapsed > deadline)
                throw new TimeoutException($"Esperé {deadline.TotalSeconds}s a: {what}");
            await Task.Delay(100);
        }
    }

    private static SagaEnvelope ProbeMessage(string purchaseId) => new(
        "probe-step", purchaseId, System.Text.Json.JsonSerializer.SerializeToElement(new { probe = 1 }));

    [SkippableFact]
    public async Task Transient_failure_recovers_on_third_attempt_with_visible_increasing_backoff()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var queueName = $"it-backoff-{Guid.NewGuid().ToString("N")[..12]}.fifo";
        var options = EmulatorAwsOptions.FromEnvironment();
        using var sqs = EmulatorAwsClients.CreateSqs(options);
        var transport = new SqsSagaTransport(sqs);
        await transport.EnsureQueueAsync(queueName, ct);

        // AC1 exacto (D-0012): transient count=2 + 2 reintentos del worker = éxito al 3er intento.
        var injector = new FailureInjector();
        injector.Inject("probe-step", FailureInjector.Transient, 2);

        var (worker, logger) = await StartWorkerAsync(queueName, injector, ct);
        try
        {
            var pid = "pur-" + Guid.NewGuid().ToString("N");
            await transport.SendAsync(queueName, ProbeMessage(pid), ct);

            await UntilAsync(async () => worker.Successes == 1, TimeSpan.FromSeconds(10),
                "el handler procesa el mensaje al 3er intento");

            RecordingLogger.Line[] retries;
            lock (logger.Lines)
                retries = [.. logger.Lines.Where(l =>
                    l.Message.Contains("intento 1/3") || l.Message.Contains("intento 2/3"))];
            Assert.Equal(2, retries.Length); // exactamente 2 reintentos logueados
            Assert.Contains(retries, r => r.Message.Contains("intento 1/3"));
            Assert.Contains(retries, r => r.Message.Contains("probe-step") && r.Message.Contains(pid));

            // Backoff visible en timestamps (AC1): espera 1 >= 0,45 s (configurada 0,5) y espera
            // 2 >= 0,9 s (configurada 1 s) — creciente entre intentos.
            var first = retries.Single(r => r.Message.Contains("intento 1/3"));
            var second = retries.Single(r => r.Message.Contains("intento 2/3"));
            var gap1 = (second.At - first.At).TotalMilliseconds;
            var gap2 = (worker.LastSuccessAt!.Value - second.At).TotalMilliseconds;
            Assert.True(gap1 >= 450, $"espera entre intento 1 y 2 fue {gap1} ms (esperaba >=450)");
            Assert.True(gap2 >= 900, $"espera entre intento 2 y éxito fue {gap2} ms (esperaba >=900)");
            Assert.True(gap2 > gap1, "las esperas deben ser crecientes");

            // El mensaje NO fue a la DLQ: la saga sobrevive al fallo transitorio.
            Assert.Empty(await transport.PeekDlqAsync(queueName, ct));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [SkippableFact]
    public async Task Persistent_failure_exhausts_retries_and_message_lands_in_inspectable_dlq()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var queueName = $"it-dlq-{Guid.NewGuid().ToString("N")[..12]}.fifo";
        var options = EmulatorAwsOptions.FromEnvironment();
        using var sqs = EmulatorAwsClients.CreateSqs(options);
        var transport = new SqsSagaTransport(sqs);
        await transport.EnsureQueueAsync(queueName, ct);

        var injector = new FailureInjector();
        injector.Inject("probe-step", FailureInjector.Persistent, 2);

        var (worker, logger) = await StartWorkerAsync(queueName, injector, ct);
        try
        {
            var pid = "pur-" + Guid.NewGuid().ToString("N");
            await transport.SendAsync(queueName, ProbeMessage(pid), ct);

            await UntilAsync(
                async () => (await transport.PeekDlqAsync(queueName, ct)).Count > 0,
                TimeSpan.FromSeconds(15), "el mensaje agota 3 intentos y llega a la DLQ");

            // DLQ inspeccionable (AC2): cuerpo original con type y purchase_id.
            var dead = (await transport.PeekDlqAsync(queueName, ct)).Single();
            Assert.Equal("probe-step", dead.Type);
            Assert.Equal(pid, dead.PurchaseId);

            // Peek NO borra: sigue ahí (VisibilityTimeout=0).
            Assert.Single(await transport.PeekDlqAsync(queueName, ct));

            // El origen quedó limpio: se borró tras encolar a la DLQ.
            var received = await sqs.ReceiveMessageAsync(new Amazon.SQS.Model.ReceiveMessageRequest
            {
                QueueUrl = await transport.EnsureQueueAsync(queueName, ct),
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
                VisibilityTimeout = 0,
            }, ct);
            Assert.True(received.Messages is not { Count: > 0 }, "la cola de origen debe quedar vacía");

            Assert.Equal(0, worker.Successes); // nunca se procesó
            lock (logger.Lines)
                Assert.Contains(logger.Lines,
                    l => l.Message.Contains("agotó 3 intentos") && l.Message.Contains(pid));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
