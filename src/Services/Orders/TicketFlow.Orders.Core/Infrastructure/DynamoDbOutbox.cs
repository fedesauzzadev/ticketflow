using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Core.Infrastructure;

/// <summary>
/// Outbox de Órdenes (D-0008 patrón 3): tabla orders_outbox, pk OUTBOX#&lt;guid&gt;. El process
/// manager persiste el comando; el relay lo publica a SQS y lo marca published (at-least-once).
/// </summary>
public sealed class DynamoDbOutbox(IAmazonDynamoDB ddb) : IOutbox
{
    public const string TableName = "orders_outbox";
    private const string PkPrefix = "OUTBOX#";

    private static readonly JsonSerializerOptions PayloadJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task InitializeAsync(CancellationToken ct) =>
        TicketFlow.Shared.Infrastructure.DynamoDbTables.EnsureAsync(ddb, TableName, ct);

    public async Task EnqueueAsync(string queueName, string type, string purchaseId, object payload, CancellationToken ct)
    {
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = TableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = PkPrefix + Guid.NewGuid().ToString("N") },
                ["queue"] = new() { S = queueName },
                ["type"] = new() { S = type },
                ["purchase_id"] = new() { S = purchaseId },
                ["payload"] = new() { S = JsonSerializer.Serialize(payload, PayloadJson) },
                ["published"] = new() { N = "0" },
                ["created_at"] = new() { S = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
            },
        }, ct);
    }

    /// <summary>
    /// Una iteración del relay: publica los pendientes (orden de creación) y los marca. Si el
    /// proceso muere entre send y mark, el reenvío duplicado lo traga el dedup id de SQS o el
    /// claim del receptor — sin doble efecto de negocio.
    /// </summary>
    public async Task<int> PublishPendingAsync(SqsSagaTransport transport, ILogger logger, CancellationToken ct)
    {
        var scan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = TableName,
            ConsistentRead = true,
            FilterExpression = "published = :zero",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":zero"] = new() { N = "0" },
            },
        }, ct);

        var published = 0;
        foreach (var item in scan.Items.OrderBy(i => i["created_at"].S, StringComparer.Ordinal))
        {
            var queue = item["queue"].S;
            var type = item["type"].S;
            var purchaseId = item["purchase_id"].S;
            await transport.SendAsync(queue, new SagaEnvelope(
                type, purchaseId,
                JsonDocument.Parse(item["payload"].S).RootElement), ct);
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = item["pk"].S } },
                UpdateExpression = "SET published = :one",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":one"] = new() { N = "1" },
                },
            }, ct);
            published++;
            logger.LogInformation("outbox publicado: '{Type}' de compra {PurchaseId} → {Queue}",
                type, purchaseId, queue);
        }
        return published;
    }
}
