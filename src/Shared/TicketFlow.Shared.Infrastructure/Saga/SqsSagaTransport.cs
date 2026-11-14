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
/// (dedup natural de reenvíos — ventana del emulador; el guard definitivo es el CAS del process manager).
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
            MessageDeduplicationId = $"{envelope.Type}:{envelope.PurchaseId}",
        }, ct);
    }
}

/// <summary>
/// Worker base que consume una cola FIFO de la saga: receive → handler → delete solo tras éxito.
/// Si el handler falla, el mensaje NO se borra y SQS lo reentrega tras el visibility timeout
/// (handlers idempotentes por diseño: claims condicionales en DynamoDB). Sobre inválido (JSON
/// roto o type/purchase_id ausente): se descarta con log de error — frontera validada (NFR-1).
/// La base es la dueña del transporte (CS9107): los handlers usan <see cref="Transport"/>.
/// </summary>
public abstract class SqsSagaWorker : BackgroundService
{
    protected SqsSagaTransport Transport { get; }
    private readonly IAmazonSQS _sqs;
    private readonly string _queueName;
    private readonly ILogger _logger;

    protected SqsSagaWorker(IAmazonSQS sqs, SqsSagaTransport transport, string queueName, ILogger logger)
    {
        _sqs = sqs;
        Transport = transport;
        _queueName = queueName;
        _logger = logger;
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

        try
        {
            await HandleAsync(envelope, ct);
            await DeleteAsync(queueUrl, message, ct); // éxito ⇒ recién ahora se borra
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "handler de '{Type}' (compra {PurchaseId}) falló en {Queue}; " +
                                "vuelve a entregarse tras el visibility timeout",
                envelope.Type, envelope.PurchaseId, _queueName);
        }
    }

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
