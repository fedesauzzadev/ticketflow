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
                // Con asiento reservado (SEATS_RESERVED por payment-declined/step-failed, o
                // PAYMENT_APPROVED por step-failed con pago ya autorizado — D-0012): hay que compensar.
                // EXPIRED (D-0018 §5) no está: una compra ya expirada ignora el pago tardío.
                ConditionExpression =
                    "attribute_exists(pk) AND (#st = :seats_reserved OR #st = :payment_approved) " +
                    "AND attribute_not_exists(pending_failure_reason)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":reason"] = new() { S = reason },
                    [":seats_reserved"] = new() { S = PurchaseStatus.SEATS_RESERVED.ToString() },
                    [":payment_approved"] = new() { S = PurchaseStatus.PAYMENT_APPROVED.ToString() },
                },
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    public async Task<bool> TryHoldReserveAsync(string purchaseId, long expiresAtEpochSeconds,
        decimal amount, CancellationToken ct)
    {
        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(purchaseId),
                UpdateExpression = "SET #st = :to, expires_at = :exp, amount = :amt",
                // #hold: "hold" es palabra reservada de DynamoDB (hallazgo S11 — igual que status).
                ConditionExpression = "attribute_exists(pk) AND #st = :from AND #hold = :one",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#st"] = "status",
                    ["#hold"] = "hold",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":to"] = new() { S = PurchaseStatus.SEATS_RESERVED.ToString() },
                    [":from"] = new() { S = PurchaseStatus.PENDING.ToString() },
                    [":exp"] = new() { N = expiresAtEpochSeconds.ToString(CultureInfo.InvariantCulture) },
                    [":amt"] = new() { N = amount.ToString(CultureInfo.InvariantCulture) },
                    [":one"] = new() { N = "1" },
                },
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // no es PENDING (duplicado) o no es hold: camino normal
        }
    }

    public async Task<IReadOnlyList<Purchase>> ScanExpirableHoldsAsync(long nowEpochSeconds,
        long zombieCutoffEpochSeconds, CancellationToken ct)
    {
        // P4a del probe (2026-11-15-ttl-probe-verification.md): comparar expires_at en ítems que
        // no lo tienen evalúa false — las compras sin hold nunca matchean. Los claims frescos
        // quedan fuera por attribute_not_exists (D-0017 §4: la saga sigue su curso).
        // shortcut: una página por pasada — lo que exceda 1 MB lo agarra el sweep siguiente (5 s).
        var scan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = TableName,
            ConsistentRead = true,
            FilterExpression =
                "#st = :sr AND ((expires_at < :now AND attribute_not_exists(payment_initiated_at)) " +
                "OR payment_initiated_at < :zombie)",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":sr"] = new() { S = PurchaseStatus.SEATS_RESERVED.ToString() },
                [":now"] = new() { N = nowEpochSeconds.ToString(CultureInfo.InvariantCulture) },
                [":zombie"] = new() { N = zombieCutoffEpochSeconds.ToString(CultureInfo.InvariantCulture) },
            },
        }, ct);
        return scan.Items.Select(FromItem).ToList();
    }

    public Task<bool> TryExpireAsync(string purchaseId, string reason, CancellationToken ct) =>
        ExpireWithConditionAsync(purchaseId, reason,
            "attribute_exists(pk) AND #st = :from AND attribute_not_exists(payment_initiated_at)",
            zombieCutoff: null, ct);

    public Task<bool> TryExpireZombieAsync(string purchaseId, string reason, long cutoffEpochSeconds,
        CancellationToken ct) =>
        ExpireWithConditionAsync(purchaseId, reason,
            "attribute_exists(pk) AND #st = :from AND payment_initiated_at < :zombie",
            zombieCutoff: cutoffEpochSeconds, ct);

    private async Task<bool> ExpireWithConditionAsync(string purchaseId, string reason,
        string condition, long? zombieCutoff, CancellationToken ct)
    {
        try
        {
            var values = new Dictionary<string, AttributeValue>
            {
                [":to"] = new() { S = PurchaseStatus.EXPIRED.ToString() },
                [":from"] = new() { S = PurchaseStatus.SEATS_RESERVED.ToString() },
                [":reason"] = new() { S = reason },
            };
            if (zombieCutoff is { } cutoff)
                values[":zombie"] = new() { N = cutoff.ToString(CultureInfo.InvariantCulture) };
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(purchaseId),
                UpdateExpression = "SET #st = :to, failure_reason = :reason",
                ConditionExpression = condition,
                ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "status" },
                ExpressionAttributeValues = values,
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false; // ya expirada, ya avanzó, o un claim ganó la carrera (D-0017 §4)
        }
    }

    public async Task<PaymentClaimOutcome> TryClaimPaymentAsync(string purchaseId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(purchaseId),
                UpdateExpression = "SET payment_initiated_at = :now",
                // D-0017 §4/D-0018 §3: el claim exige hold SEATS_RESERVED vigente y sin claim
                // previo — gana quien CASé primero, sin relojes distribuidos. (#hold reservada.)
                ConditionExpression =
                    "attribute_exists(pk) AND #st = :sr AND #hold = :one AND expires_at > :now " +
                    "AND attribute_not_exists(payment_initiated_at)",
                ExpressionAttributeNames = new Dictionary<string, string>
                {
                    ["#st"] = "status",
                    ["#hold"] = "hold",
                },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":now"] = new() { N = now.ToString(CultureInfo.InvariantCulture) },
                    [":sr"] = new() { S = PurchaseStatus.SEATS_RESERVED.ToString() },
                    [":one"] = new() { N = "1" },
                },
            }, ct);
            return PaymentClaimOutcome.Claimed;
        }
        catch (ConditionalCheckFailedException)
        {
            // Desambiguación por lectura consistente: el código HTTP depende del porqué.
            var purchase = await GetAsync(purchaseId, ct);
            if (purchase is null) return PaymentClaimOutcome.NotFound;
            if (purchase.Status == PurchaseStatus.EXPIRED) return PaymentClaimOutcome.Expired;
            // El claim vigente gana a la expiración del reloj (D-0017 §4): la saga sigue.
            if (purchase.PaymentInitiatedAt is not null && purchase.Status == PurchaseStatus.SEATS_RESERVED)
                return PaymentClaimOutcome.AlreadyClaimed;
            // Vencida de hecho (expires_at <= now, sin claim): el sweeper la transicionará —
            // para el evaluador la reserva YA expiró (409 hold-expired, sin esperar el sweep).
            if (purchase.Hold && purchase.Status == PurchaseStatus.SEATS_RESERVED
                && purchase.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return PaymentClaimOutcome.Expired;
            return PaymentClaimOutcome.NotClaimable;
        }
    }

    private static Dictionary<string, AttributeValue> Key(string purchaseId) =>
        new() { ["pk"] = new AttributeValue { S = PkPrefix + purchaseId } };

    private static Dictionary<string, AttributeValue> ToItem(Purchase p)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new AttributeValue { S = PkPrefix + p.PurchaseId },
            ["event_id"] = new AttributeValue { S = p.EventId },
            ["seats"] = new AttributeValue { N = p.Seats.ToString(CultureInfo.InvariantCulture) },
            ["pan"] = new AttributeValue { S = p.Pan },
            ["status"] = new AttributeValue { S = p.Status.ToString() },
            ["created_at"] = new AttributeValue { S = p.CreatedAt.ToString("O", CultureInfo.InvariantCulture) },
            ["idempotency_key"] = new AttributeValue { S = p.IdempotencyKey },
        };
        // Atributos del hold SOLO en holds (D-0017 §1): las compras normales quedan igual que V1.
        if (p.Owner.Length > 0) item["owner_sub"] = new AttributeValue { S = p.Owner };
        if (p.Hold) item["hold"] = new AttributeValue { N = "1" };
        if (p.Amount is { } amount)
            item["amount"] = new AttributeValue { N = amount.ToString(CultureInfo.InvariantCulture) };
        return item;
    }

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
            : null,
        Hold: item.GetValueOrDefault("hold")?.N == "1",
        ExpiresAt: EpochOf(item, "expires_at"),
        PaymentInitiatedAt: EpochOf(item, "payment_initiated_at"),
        Amount: item.GetValueOrDefault("amount")?.N is { } amt
            ? decimal.Parse(amt, CultureInfo.InvariantCulture)
            : null,
        Owner: item.GetValueOrDefault("owner_sub")?.S ?? "");

    private static long? EpochOf(Dictionary<string, AttributeValue> item, string attr) =>
        item.GetValueOrDefault(attr)?.N is { } n ? long.Parse(n, CultureInfo.InvariantCulture) : null;
}
