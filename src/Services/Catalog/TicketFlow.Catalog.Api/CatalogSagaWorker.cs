using Amazon.SQS;
using Microsoft.Extensions.Logging;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Catalog.Api;

/// <summary>
/// Worker de la saga del Catálogo (D-0004): consume ticketflow-catalog-commands.fifo, delega en
/// CatalogSagaHandler (mutación + invalidación de cache) y publica los eventos resultantes a
/// Órdenes. Delgado a propósito: la lógica se testea sin mensajería.
/// </summary>
public sealed class CatalogSagaWorker(
    IAmazonSQS sqs,
    SqsSagaTransport transport,
    CatalogSagaHandler handler,
    TicketFlow.Shared.Infrastructure.FailureInjector failureInjector,
    ILogger<CatalogSagaWorker> logger)
    : SqsSagaWorker(sqs, transport, SagaQueues.CatalogCommands, logger, failureInjector)
{
    protected override async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        foreach (var @event in await handler.HandleAsync(envelope, ct))
            await Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
    }
}
