using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using TicketFlow.Payments.Core.Application;

namespace TicketFlow.Payments.Core.Infrastructure;

/// <summary>Tabla payments, pk PAYMENT#&lt;purchase_id&gt; — una autorización por compra (idempotente).</summary>
public sealed class DynamoDbPaymentRepository(IAmazonDynamoDB ddb) : IPaymentRepository
{
    public const string TableName = "payments";
    private const string PkPrefix = "PAYMENT#";

    public Task InitializeAsync(CancellationToken ct) =>
        TicketFlow.Shared.Infrastructure.DynamoDbTables.EnsureAsync(ddb, TableName, ct);

    public async Task<bool> SaveIfNewAsync(Payment payment, CancellationToken ct)
    {
        try
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = ToItem(payment),
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // ya decidido para esta compra
        }
    }

    public async Task<Payment?> GetAsync(string purchaseId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = PkPrefix + purchaseId },
            },
            ConsistentRead = true,
        }, ct);
        return response.Item is not { Count: > 0 } ? null : FromItem(response.Item); // L-004
    }

    private static Dictionary<string, AttributeValue> ToItem(Payment p) => new()
    {
        ["pk"] = new() { S = PkPrefix + p.PurchaseId },
        ["approved"] = new() { N = p.Approved ? "1" : "0" },
        ["reason"] = new() { S = p.Reason ?? "" },
        ["amount"] = new() { N = p.Amount.ToString(CultureInfo.InvariantCulture) },
        ["pan_last4"] = new() { S = p.PanLast4 },
        ["decided_at"] = new() { S = p.DecidedAt.ToString("O", CultureInfo.InvariantCulture) },
    };

    private static Payment FromItem(Dictionary<string, AttributeValue> item) => new(
        item["pk"].S[PkPrefix.Length..],
        item["approved"].N == "1",
        item["reason"].S is { Length: > 0 } reason ? reason : null,
        decimal.Parse(item["amount"].N, CultureInfo.InvariantCulture),
        item["pan_last4"].S,
        DateTimeOffset.Parse(item["decided_at"].S, CultureInfo.InvariantCulture));
}
