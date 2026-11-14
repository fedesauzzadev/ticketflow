using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using TicketFlow.Orders.Core.Application;

namespace TicketFlow.Orders.Core.Infrastructure;

/// <summary>
/// Idempotency-Key store sobre DynamoDB del emulador (D-0009): tabla orders_idempotency,
/// pk IDEMPOTENCY#&lt;key&gt;. Claim = PutItem condicional attribute_not_exists (primitiva
/// verificada por el TL); peek = GetItem con lectura consistente.
/// </summary>
public sealed class DynamoDbIdempotencyStore(IAmazonDynamoDB ddb) : IIdempotencyStore
{
    public const string TableName = "orders_idempotency";
    private const string PkPrefix = "IDEMPOTENCY#";

    public Task InitializeAsync(CancellationToken ct) =>
        TicketFlow.Shared.Infrastructure.DynamoDbTables.EnsureAsync(ddb, TableName, ct);

    public async Task<IdempotencyClaim?> FindAsync(string idempotencyKey, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = Key(idempotencyKey),
            ConsistentRead = true,
        }, ct);
        // El emulador devuelve Item=null (no dict vacío) cuando la key no existe (L-004).
        return response.Item is not { Count: > 0 }
            ? null
            : new IdempotencyClaim(response.Item["purchase_id"].S, response.Item["request_hash"].S);
    }

    public async Task<bool> TryClaimAsync(string idempotencyKey, string requestHash, string purchaseId,
        CancellationToken ct)
    {
        try
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = PkPrefix + idempotencyKey },
                    ["purchase_id"] = new() { S = purchaseId },
                    ["request_hash"] = new() { S = requestHash },
                    ["created_at"] = new() { S = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                },
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // otra request con la misma key ganó — el peek del caller decide replay/mismatch
        }
    }

    private static Dictionary<string, AttributeValue> Key(string idempotencyKey) =>
        new() { ["pk"] = new AttributeValue { S = PkPrefix + idempotencyKey } };
}
