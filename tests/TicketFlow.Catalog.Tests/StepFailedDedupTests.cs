using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Dedup del transporte para step-failed (F-V1-R2-01) contra emulador real: el id incluye
/// el paso fallado, entonces DOS fallos de pasos DISTINTOS de la MISMA compra (paso +
/// compensación) llegan ambos a Órdenes — antes colisionaban en {type}:{purchase_id} y
/// SQS FIFO tragaba el segundo en silencio. El reenvío IDÉNTICO del mismo paso sigue
/// deduplicado (dedup natural de reenvíos de D-0006/D-0012 intacto).
/// </summary>
public sealed class StepFailedDedupTests
{
    private static SagaEnvelope StepFailed(string purchaseId, string step) =>
        new(SagaTypes.StepFailed, purchaseId,
            JsonSerializer.SerializeToElement(new StepFailedEvent(step, "el paso falló"), SagaJson.Options));

    /// <summary>Recibe (y borra) todo lo entregado en la cola; null si no hay nada visible.</summary>
    private static async Task<List<SagaEnvelope>> ReceiveAllAsync(IAmazonSQS sqs, string queueUrl,
        CancellationToken ct)
    {
        var delivered = new List<SagaEnvelope>();
        for (var poll = 0; poll < 15; poll++)
        {
            var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                WaitTimeSeconds = 0,
            }, ct);
            if (received.Messages is not { Count: > 0 }) break; // como L-005: Messages=null si vacía
            foreach (var message in received.Messages)
            {
                delivered.Add(SagaJson.Deserialize(message.Body));
                await sqs.DeleteMessageAsync(new DeleteMessageRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle,
                }, ct);
            }
        }
        return delivered;
    }

    [SkippableFact]
    public async Task Two_step_failed_of_different_steps_same_purchase_are_both_delivered()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var options = EmulatorAwsOptions.FromEnvironment();
        using var sqs = EmulatorAwsClients.CreateSqs(options);
        var transport = new SqsSagaTransport(sqs);
        // Cola dedicada por corrida (L-007/L-008): sin interferencia de workers de otros hosts.
        var queueName = $"it-stepfailed-dedup-{Guid.NewGuid().ToString("N")[..12]}.fifo";
        var url = await transport.EnsureQueueAsync(queueName, ct);

        // 1) F-V1-R2-01: paso + compensación de la MISMA compra — DOS fallos, DOS pasos.
        //    Sin el paso en el dedup id, el segundo colisiona con el primero y no llega.
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        await transport.SendAsync(queueName, StepFailed(purchaseId, SagaTypes.AuthorizePayment), ct);
        await transport.SendAsync(queueName, StepFailed(purchaseId, SagaTypes.ReleaseSeats), ct);
        var both = await ReceiveAllAsync(sqs, url, ct);
        Assert.Equal(2, both.Count);
        Assert.Contains(both, e => e.PayloadAs<StepFailedEvent>().Step == SagaTypes.AuthorizePayment);
        Assert.Contains(both, e => e.PayloadAs<StepFailedEvent>().Step == SagaTypes.ReleaseSeats);

        // 2) Dedup natural intacto: reenvío IDÉNTICO (mismo paso, misma compra) → pasa UNO.
        var purchaseId2 = "pur-" + Guid.NewGuid().ToString("N");
        var envelope = StepFailed(purchaseId2, SagaTypes.ReleaseSeats);
        await transport.SendAsync(queueName, envelope, ct);
        await transport.SendAsync(queueName, envelope, ct);
        var deduped = await ReceiveAllAsync(sqs, url, ct);
        var delivered2 = deduped.Count(e => e.PurchaseId == purchaseId2);
        Assert.Equal(1, delivered2);
    }
}
