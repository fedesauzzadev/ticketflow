using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;

namespace TicketFlow.Orders.Core.Infrastructure;

/// <summary>
/// Repositorio de compras sobre DynamoDB del emulador (D-0005): tabla purchases, pk
/// PURCHASE#&lt;purchase_id&gt;. Transiciones CAS por ConditionExpression — el guard de duplicados.
/// </summary>
public sealed class DynamoDbPurchaseRepository(IAmazonDynamoDB ddb) : IPurchaseRepository
{
    public const string TableName = "purchases";
    private const string PkPrefix = "PURCHASE#";

    private static readonly JsonSerializerOptions TicketIdsJson =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public Task InitializeAsync(CancellationToken ct) =>
        TicketFlow.Shared.Infrastructure.DynamoDbTables.EnsureAsync(ddb, TableName, ct);

    public async Task CreateAsync(Purchase purchase, CancellationToken ct)
    {
        try
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = ToItem(purchase),
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Ids GUID: colisión imposible en la práctica; si ocurre, el contrato exige fallo explícito.
            throw new InvalidOperationException(
                $"Id de compra duplicado '{purchase.PurchaseId}' (condición attribute_not_exists).");
        }
    }

    public async Task<Purchase?> GetAsync(string purchaseId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = Key(purchaseId),
            ConsistentRead = true,
        }, ct);
        // El emulador devuelve Item=null (no dict vacío) cuando la key no existe (L-004).
        return response.Item is not { Count: > 0 } ? null : FromItem(response.Item);
    }

    public async Task<bool> TryTransitionAsync(string purchaseId, PurchaseStatus from, PurchaseStatus to,
        string? reason = null, IReadOnlyList<string>? ticketIds = null, CancellationToken ct = default)
    {
        var update = "SET #st = :to";
        var values = new Dictionary<string, AttributeValue>
        {
            [":to"] = new() { S = to.ToString() },
            [":from"] = new() { S = from.ToString() },
        };
        if (reason is not null)
        {
            update += ", failure_reason = :reason";
            values[":reason"] = new() { S = reason };
        }
        if (ticketIds is not null)
        {
            update += ", ticket_ids = :tickets";
            values[":tickets"] = new() { S = JsonSerializer.Serialize(ticketIds, TicketIdsJson) };
        }

        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(purchaseId),
                UpdateExpression = update,
                ConditionExpression = "attribute_exists(pk) AND #st = :from",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status" },
                ExpressionAttributeValues = values,
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // ya cambió (o no existe): duplicado/orden fuera de secuencia
        }
    }

    public async Task<bool> TryNoteFailureAsync(string purchaseId, string reason, CancellationToken ct)
    {
        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(purchaseId),
                UpdateExpression = "SET pending_failure_reason = :reason",
                ConditionExpression =
                    "attribute_exists(pk) AND #st = :seats_reserved AND attribute_not_exists(pending_failure_reason)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":reason"] = new() { S = reason },
                    [":seats_reserved"] = new() { S = PurchaseStatus.SEATS_RESERVED.ToString() },
                },
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private static Dictionary<string, AttributeValue> Key(string purchaseId) =>
        new() { ["pk"] = new AttributeValue { S = PkPrefix + purchaseId } };

    private static Dictionary<string, AttributeValue> ToItem(Purchase p) => new()
    {
        ["pk"] = new AttributeValue { S = PkPrefix + p.PurchaseId },
        ["event_id"] = new AttributeValue { S = p.EventId },
        ["seats"] = new AttributeValue { N = p.Seats.ToString(CultureInfo.InvariantCulture) },
        ["pan"] = new AttributeValue { S = p.Pan },
        ["status"] = new AttributeValue { S = p.Status.ToString() },
        ["created_at"] = new AttributeValue { S = p.CreatedAt.ToString("O", CultureInfo.InvariantCulture) },
        ["idempotency_key"] = new AttributeValue { S = p.IdempotencyKey },
    };

    private static Purchase FromItem(Dictionary<string, AttributeValue> item) => new(
        item["pk"].S[PkPrefix.Length..],
        item["event_id"].S,
        int.Parse(item["seats"].N, CultureInfo.InvariantCulture),
        item["pan"].S,
        Enum.Parse<PurchaseStatus>(item["status"].S),
        DateTimeOffset.Parse(item["created_at"].S, CultureInfo.InvariantCulture),
        item.GetValueOrDefault("idempotency_key")?.S ?? "",
        item.GetValueOrDefault("failure_reason")?.S,
        item.GetValueOrDefault("pending_failure_reason")?.S,
        item.GetValueOrDefault("ticket_ids")?.S is { } json
            ? JsonSerializer.Deserialize<IReadOnlyList<string>>(json, TicketIdsJson)
            : null);
}
