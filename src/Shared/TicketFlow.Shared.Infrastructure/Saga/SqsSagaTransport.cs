using System.Text.Json;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TicketFlow.Shared.Infrastructure.Saga;

/// <summary>
/// Transporte de la saga sobre SQS FIFO del emulador (D-0006). Crea las colas idempotentemente
/// (tolerante a carreras de arranque — el emulador da 500 crudos en concurrencia), envía con
/// MessageGroupId=purchase_id (orden por compra) y MessageDeduplicationId={type}:{purchase_id}
/// (dedup natural de reenvíos — ventana del emulador; el guard definitivo es el CAS del process
/// manager). El step-failed añade el paso fallado al id (F-V1-R2-01): dos fallos de pasos
/// distintos de la misma compra deben llegar ambos a Órdenes.
/// </summary>
public sealed class SqsSagaTransport(IAmazonSQS sqs)
{
    private readonly Dictionary<string, string> _queueUrls = [];

    public async Task<string> EnsureQueueAsync(string queueName, CancellationToken ct)
    {
        if (_queueUrls.TryGetValue(queueName, out var cached)) return cached;

        for (var attempt = 0; attempt < 20; attempt++) // ~5 s de presupuesto
        {
            try
            {
                var url = (await sqs.GetQueueUrlAsync(new GetQueueUrlRequest { QueueName = queueName }, ct)).QueueUrl;
                _queueUrls[queueName] = url;
                return url;
            }
            catch (QueueDoesNotExistException)
            {
                try
                {
                    var created = await sqs.CreateQueueAsync(new CreateQueueRequest
                    {
                        QueueName = queueName,
                        Attributes = new Dictionary<string, string>
                        {
                            ["FifoQueue"] = "true",
                            ["ContentBasedDeduplication"] = "true",
                        },
                    }, ct);
                    _queueUrls[queueName] = created.QueueUrl;
                    return created.QueueUrl;
                }
                catch (Exception ex) when (ex is QueueNameExistsException or AmazonServiceException)
                {
                    // Carrera de arranque: otro proceso la creó — se reintenta el GetQueueUrl.
                }
            }
            catch (AmazonServiceException)
            {
                // Transient del emulador: reintentar.
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"La cola {queueName} no quedó disponible en 5 s.");
    }

    public async Task SendAsync(string queueName, SagaEnvelope envelope, CancellationToken ct)
    {
        var url = await EnsureQueueAsync(queueName, ct);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = SagaJson.SerializeEnvelope(envelope),
            MessageGroupId = envelope.PurchaseId,
            // F-V1-R2-01: sin el paso fallado, el step-failed de la compensación colisiona con
            // el step-failed previo de la MISMA compra y SQS FIFO lo traga en silencio.
            MessageDeduplicationId = envelope.Type == SagaTypes.StepFailed
                ? $"{envelope.Type}:{envelope.PurchaseId}:{FailedStepOf(envelope)}"
                : $"{envelope.Type}:{envelope.PurchaseId}",
        }, ct);
    }

    /// <summary>
    /// Paso fallado del step-failed para su dedup id (F-V1-R2-01). Id único si el payload no
    /// lo da: un step-failed nunca debe perderse por dedup del transporte (guard = CAS).
    /// </summary>
    private static string FailedStepOf(SagaEnvelope envelope)
    {
        try { return envelope.PayloadAs<StepFailedEvent>().Step; }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>DLQ aplicativa de una cola (D-0012): &lt;cola&gt;-dlq.fifo — inspeccionable con GET /admin/dlq.</summary>
    public static string DlqQueueOf(string queueName) =>
        queueName.EndsWith(".fifo", StringComparison.Ordinal)
            ? queueName[..^".fifo".Length] + "-dlq.fifo"
            : queueName + "-dlq.fifo";

    /// <summary>
    /// Peek de la DLQ (D-0012) SIN borrar, enumerándola COMPLETA (D-0020, F-V1-R3-01): loop
    /// de recepciones de a 10 con VisibilityTimeout=2 — la ventana corta rota las páginas
    /// (con 0 el emulador devuelve siempre la misma primera) — deduplicando por MessageId.
    /// Una pasada sin aportes deja al lote anterior invisible: la pasada inmediata siguiente
    /// cava más profundo; dos consecutivas sin aportes = DLQ completa (tope 50 pasadas / 500
    /// entradas: la DLQ es evidencia de fallos, no cola de trabajo). Al volver, todo está de
    /// nuevo visible (se espera la expiración de la ventana): el peek no muta la cola.
    /// </summary>
    public async Task<IReadOnlyList<SagaDlqEntry>> PeekDlqAsync(string consumedQueueName, CancellationToken ct)
    {
        var url = await EnsureQueueAsync(DlqQueueOf(consumedQueueName), ct);
        var seen = new Dictionary<string, SagaDlqEntry>();
        var idlePasses = 0;
        for (var pass = 0; pass < 50 && seen.Count < 500; pass++)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = url,
                MaxNumberOfMessages = 10,
                VisibilityTimeout = 2,
            }, ct);
            var added = 0;
            if (response.Messages is { Count: > 0 }) // como L-005: Messages=null si vacía
                foreach (var message in response.Messages)
                    if (seen.TryAdd(message.MessageId, SagaDlqEntry.From(message)))
                        added++;
            if (added > 0)
            {
                idlePasses = 0;
                continue;
            }
            if (++idlePasses >= 2) break; // dos pasadas seguidas sin aportes: está completa
        }
        if (seen.Count > 0)
            await Task.Delay(TimeSpan.FromSeconds(2.2), ct); // ventana vencida: todo vuelve a verse
        return [.. seen.Values];
    }
}

/// <summary>Vista de un mensaje muerto para GET /admin/dlq (sin exponer el receipt handle).</summary>
public sealed record SagaDlqEntry(string Type, string PurchaseId, string? Payload, string MessageId)
{
    public static SagaDlqEntry From(Message message)
    {
        try
        {
            var envelope = SagaJson.Deserialize(message.Body);
            return new SagaDlqEntry(envelope.Type, envelope.PurchaseId,
                envelope.Payload?.GetRawText(), message.MessageId);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Frontera (NFR-1): cuerpo corrupto se reporta acotado, no rompe el endpoint.
            var body = message.Body.Length <= 200 ? message.Body : message.Body[..200];
            return new SagaDlqEntry("(inválido)", "", body, message.MessageId);
        }
    }
}

/// <summary>
/// Worker base que consume una cola FIFO de la saga: receive → (backoff) → handler → delete
/// solo tras éxito. Backoff D-0012: hasta 3 intentos por entrega (1 original + 2 reintentos,
/// esperas 0,5 s / 1 s crecientes) — seguro porque los handlers son idempotentes. Agotados
/// los intentos: DLQ aplicativa &lt;cola&gt;-dlq.fifo + evento step-failed → Órdenes marca la
/// compra FAILED (y compensa si había reserva) + delete del origen: nada queda en limbo.
/// Antes de cada intento consulta el <see cref="FailureInjector"/> (fallo inyectable, D-0012).
/// Sobre inválido (JSON roto o type/purchase_id ausente): descartado con log — frontera (NFR-1).
/// La base es la dueña del transporte (CS9107): los handlers usan <see cref="Transport"/>.
/// </summary>
public abstract class SqsSagaWorker : BackgroundService
{
    /// <summary>Esperas crecientes entre intentos (D-0012): AC1 exige backoff visible en timestamps.</summary>
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1)];

    protected SqsSagaTransport Transport { get; }
    /// <summary>Los workers de comandos reportan su fallo terminal a Órdenes (evento step-failed).</summary>
    protected virtual bool EmitsStepFailed => true;

    private readonly IAmazonSQS _sqs;
    private readonly string _queueName;
    private readonly ILogger _logger;
    private readonly FailureInjector? _failureInjector;

    protected SqsSagaWorker(IAmazonSQS sqs, SqsSagaTransport transport, string queueName, ILogger logger,
        FailureInjector? failureInjector = null)
    {
        _sqs = sqs;
        Transport = transport;
        _queueName = queueName;
        _logger = logger;
        _failureInjector = failureInjector;
    }

    protected abstract Task HandleAsync(SagaEnvelope envelope, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? queueUrl = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                queueUrl ??= await Transport.EnsureQueueAsync(_queueName, stoppingToken);
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 5,
                    WaitTimeSeconds = 1,
                }, stoppingToken);
                // El emulador devuelve Messages=null (no lista vacía) cuando no hay mensajes (como L-004).
                if (response.Messages is not { Count: > 0 })
                {
                    await Task.Delay(200, stoppingToken); // anti busy-loop si el emulador no hace long polling
                    continue;
                }
                foreach (var message in response.Messages)
                    await HandleOneAsync(queueUrl, message, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "worker de {Queue} falló (receive/ensure); reintenta en 1 s", _queueName);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    private async Task HandleOneAsync(string queueUrl, Message message, CancellationToken ct)
    {
        SagaEnvelope? envelope;
        try
        {
            envelope = SagaJson.Deserialize(message.Body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogError(ex, "mensaje de saga inválido en {Queue}: descartado", _queueName);
            await DeleteAsync(queueUrl, message, ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(envelope.Type) || string.IsNullOrWhiteSpace(envelope.PurchaseId))
        {
            _logger.LogError("sobre de saga incompleto en {Queue} (type/purchase_id): descartado", _queueName);
            await DeleteAsync(queueUrl, message, ct);
            return;
        }

        // Backoff D-0012: 3 intentos por entrega con esperas crecientes; agotados → DLQ aplicativa.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (_failureInjector is { } injector && injector.TryConsume(envelope.Type) is { } injected)
                    throw new InjectedFailureException(
                        $"fallo inyectado ({injected.Mode}) en el paso '{injected.Step}' " +
                        $"(compra {envelope.PurchaseId})");

                await HandleAsync(envelope, ct);
                await DeleteAsync(queueUrl, message, ct); // éxito ⇒ recién ahora se borra
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown del host: no es fallo del handler, no cuenta para el backoff
            }
            catch (Exception ex) when (attempt <= RetryDelays.Length)
            {
                _logger.LogWarning(ex,
                    "intento {Attempt}/3 de '{Type}' (compra {PurchaseId}) falló en {Queue}; " +
                    "reintento en {Delay} ms (backoff)",
                    attempt, envelope.Type, envelope.PurchaseId, _queueName,
                    RetryDelays[attempt - 1].TotalMilliseconds);
                await Task.Delay(RetryDelays[attempt - 1], ct);
            }
            catch (Exception ex)
            {
                await DeadLetterAsync(queueUrl, message, envelope, ex, ct);
                return;
            }
        }
    }

    /// <summary>
    /// Agotados los 3 intentos (D-0012): cuerpo original → DLQ aplicativa, evento step-failed →
    /// Órdenes (FAILED terminal + compensación) y recién entonces delete del origen. Si algo de
    /// esto falla (p. ej. emulador caído), el origen NO se borra: la entrega se reintenta.
    /// </summary>
    private async Task DeadLetterAsync(string queueUrl, Message message, SagaEnvelope envelope, Exception cause,
        CancellationToken ct)
    {
        var dlqName = SqsSagaTransport.DlqQueueOf(_queueName);
        await Transport.SendAsync(dlqName, envelope, ct);

        if (EmitsStepFailed)
            await Transport.SendAsync(SagaQueues.OrdersEvents, new SagaEnvelope(
                SagaTypes.StepFailed, envelope.PurchaseId,
                JsonSerializer.SerializeToElement(
                    new StepFailedEvent(envelope.Type, ReasonOf(envelope, cause)), SagaJson.Options)), ct);

        await DeleteAsync(queueUrl, message, ct);
        _logger.LogError(
            "mensaje '{Type}' (compra {PurchaseId}) agotó 3 intentos en {Queue}: → {Dlq}" +
            (EmitsStepFailed ? " + evento step-failed a Órdenes" : ""),
            envelope.Type, envelope.PurchaseId, _queueName, dlqName);
    }

    // Sin internals del downstream en el motivo (viaja a la respuesta HTTP de la compra, NFR-4).
    private static string ReasonOf(SagaEnvelope envelope, Exception cause) =>
        $"el paso '{envelope.Type}' falló tras 3 intentos ({cause.GetType().Name})";

    private async Task DeleteAsync(string queueUrl, Message message, CancellationToken ct)
    {
        try
        {
            await _sqs.DeleteMessageAsync(new DeleteMessageRequest
            {
                QueueUrl = queueUrl,
                ReceiptHandle = message.ReceiptHandle,
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "delete de mensaje en {Queue} falló (se reentregará)", _queueName);
        }
    }
}
