using Amazon.SQS;
using Microsoft.Extensions.Logging;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Api;

/// <summary>
/// Worker de eventos de la saga: consume ticketflow-orders-events.fifo y alimenta el process
/// manager (D-0004). Los comandos que éste emite van al outbox; el relay los publica.
/// Si SUS reintentos se agotan, el mensaje va a la DLQ de este servicio — pero NO emite
/// step-failed (se auto-alimentarían: el fallo ya quedó inspeccionable en la propia DLQ).
/// </summary>
public sealed class OrdersEventsWorker(
    IAmazonSQS sqs,
    SqsSagaTransport transport,
    SagaProcessManager processManager,
    TicketFlow.Shared.Infrastructure.FailureInjector failureInjector,
    ILogger<OrdersEventsWorker> logger)
    : SqsSagaWorker(sqs, transport, SagaQueues.OrdersEvents, logger, failureInjector)
{
    protected override bool EmitsStepFailed => false; // D-0012: sin loop self-feed

    protected override Task HandleAsync(SagaEnvelope envelope, CancellationToken ct) =>
        processManager.HandleAsync(envelope, ct);
}

/// <summary>
/// Relay del outbox (D-0008 patrón 3): publica los comandos pendientes en un loop corto.
/// shortcut: scan por iteración — suficiente para el volumen del MVP, meter índice en V1 si escala.
/// </summary>
public sealed class OutboxRelayWorker(
    DynamoDbOutbox outbox,
    SqsSagaTransport transport,
    ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var queuesEnsured = false;
        logger.LogInformation("relay del outbox activo");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Las 4 colas existen antes de que nadie publique (creación idempotente, D-0006).
                if (!queuesEnsured)
                {
                    foreach (var queue in SagaQueues.All)
                        await transport.EnsureQueueAsync(queue, stoppingToken);
                    queuesEnsured = true;
                }

                var published = await outbox.PublishPendingAsync(transport, logger, stoppingToken);
                if (published == 0)
                    await Task.Delay(200, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "iteración del relay falló; reintenta en 1 s");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
