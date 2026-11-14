using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using TicketFlow.Ticketing.Core.Application;

namespace TicketFlow.Ticketing.Core.Infrastructure;

/// <summary>Tabla tickets: pk TICKET#&lt;id&gt; por entrada + pk ISSUANCE#&lt;purchase_id&gt; como claim.</summary>
public sealed class DynamoDbTicketRepository(IAmazonDynamoDB ddb) : ITicketRepository
{
    public const string TableName = "tickets";
    private const string TicketPrefix = "TICKET#";
    private const string IssuancePrefix = "ISSUANCE#";

    private static readonly JsonSerializerOptions IdsJson = new();

    public Task InitializeAsync(CancellationToken ct) =>
        TicketFlow.Shared.Infrastructure.DynamoDbTables.EnsureAsync(ddb, TableName, ct);

    public async Task SaveAsync(Ticket ticket, CancellationToken ct) =>
        await ddb.PutItemAsync(new PutItemRequest
        {
            TableName = TableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = TicketPrefix + ticket.Id },
                ["purchase_id"] = new() { S = ticket.PurchaseId },
                ["event_id"] = new() { S = ticket.EventId },
                ["seat"] = new() { N = ticket.Seat.ToString(CultureInfo.InvariantCulture) },
                ["qr_s3_key"] = new() { S = ticket.QrS3Key },
            },
        }, ct);

    public async Task<Ticket?> GetAsync(string ticketId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = TicketPrefix + ticketId } },
            ConsistentRead = true,
        }, ct);
        var item = response.Item;
        if (item is not { Count: > 0 }) return null; // L-004
        return new Ticket(
            item["pk"].S[TicketPrefix.Length..],
            item["purchase_id"].S,
            item["event_id"].S,
            int.Parse(item["seat"].N, CultureInfo.InvariantCulture),
            item["qr_s3_key"].S);
    }

    public async Task<IReadOnlyList<string>?> ClaimIssuanceAsync(
        string purchaseId, IReadOnlyList<string> ticketIds, CancellationToken ct)
    {
        try
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = IssuancePrefix + purchaseId },
                    ["ticket_ids"] = new() { S = JsonSerializer.Serialize(ticketIds, IdsJson) },
                    ["created_at"] = new() { S = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                },
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct);
            return null; // ganó el claim: aún no existía emisión
        }
        catch (ConditionalCheckFailedException)
        {
            return await GetIssuedTicketIdsAsync(purchaseId, ct) ?? [];
        }
    }

    public async Task<IReadOnlyList<string>?> GetIssuedTicketIdsAsync(string purchaseId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new() { S = IssuancePrefix + purchaseId } },
            ConsistentRead = true,
        }, ct);
        var item = response.Item;
        return item is not { Count: > 0 }
            ? null
            : JsonSerializer.Deserialize<IReadOnlyList<string>>(item["ticket_ids"].S, IdsJson);
    }
}
