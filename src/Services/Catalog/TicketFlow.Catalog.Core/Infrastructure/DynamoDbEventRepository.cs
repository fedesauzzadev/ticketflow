using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;

namespace TicketFlow.Catalog.Core.Infrastructure;

/// <summary>
/// Repositorio del catálogo sobre DynamoDB del emulador (D-0005).
/// Tabla catalog_events con pk discriminada "EVENT#&lt;id&gt;". El seed es condicional
/// (attribute_not_exists): reiniciar el microservicio NO pisa el estado evolucionado
/// — el estado vive en el emulador, no en el proceso (H-01 AC4).
/// </summary>
public sealed class DynamoDbEventRepository(IAmazonDynamoDB ddb) : IEventRepository, ISeatReservationStore
{
    public const string TableName = "catalog_events";
    private const string PkPrefix = "EVENT#";

    /// <summary>Creación idempotente de tabla + seed condicional. Se llama al arranque del host.</summary>
    public async Task InitializeAsync(IReadOnlyList<CatalogEvent> seed, CancellationToken ct)
    {
        await EnsureTableAsync(ct);
        foreach (var evt in seed)
        {
            try
            {
                await ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = TableName,
                    Item = ToItem(evt),
                    ConditionExpression = "attribute_not_exists(pk)",
                }, ct);
            }
            catch (ConditionalCheckFailedException)
            {
                // Ya existía de una corrida anterior: se conserva el estado actual.
            }
        }
    }

    /// <summary>Escritura incondicional (uso interno: tests y mutaciones directas del catálogo).</summary>
    public async Task PutAsync(CatalogEvent evt, CancellationToken ct) =>
        await ddb.PutItemAsync(new PutItemRequest { TableName = TableName, Item = ToItem(evt) }, ct);

    public async Task<IReadOnlyList<CatalogEvent>> ListAsync(CancellationToken ct)
    {
        // La tabla también aloja RESERVATION#<purchase_id> (S02): el catálogo lista solo EVENT#.
        var scan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = TableName,
            ConsistentRead = true,
            FilterExpression = "begins_with(pk, :prefix)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":prefix"] = new AttributeValue { S = PkPrefix },
            },
        }, ct);
        return scan.Items.Select(FromItem).OrderBy(e => e.Date).ToList();
    }

    public async Task<CatalogEvent?> GetByIdAsync(string eventId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = Key(eventId),
            ConsistentRead = true,
        }, ct);
        // El emulador devuelve Item=null (no dict vacío) cuando la key no existe.
        return response.Item is not { Count: > 0 } ? null : FromItem(response.Item);
    }

    // --- Saga (S02, D-0004/D-0005): reserva y liberación de asientos -------------

    private const string ReservationPrefix = "RESERVATION#";

    /// <summary>
    /// Reserva idempotente de asientos (comando reserve-seats): 1) claim condicional
    /// RESERVATION#purchase_id (attribute_not_exists ⇒ un solo efecto por compra, hook de H-03);
    /// 2) decremento atómico anti-oversell (available >= seats, primitiva verificada por el TL).
    /// </summary>
    public async Task<ReserveResult> ReserveAsync(string eventId, string purchaseId, int seats, CancellationToken ct)
    {
        try
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = ReservationPrefix + purchaseId },
                    ["event_id"] = new() { S = eventId },
                    ["seats"] = new() { N = seats.ToString(CultureInfo.InvariantCulture) },
                    ["state"] = new() { S = "RESERVED" },
                },
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Duplicado: el intento previo ya dejó su efecto único. Re-leer para el ack correcto.
            return await GetReservationStateAsync(purchaseId, ct) == "RESERVED"
                ? new ReserveResult(ReservationOutcome.DuplicateReserved, await PriceOfAsync(eventId, ct))
                : new ReserveResult(ReservationOutcome.Unavailable, 0m); // intento previo fallido
        }

        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(eventId),
                UpdateExpression = "SET available = available - :n",
                ConditionExpression = "available >= :n",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":n"] = new() { N = seats.ToString(CultureInfo.InvariantCulture) },
                },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            await MarkReservationAsync(purchaseId, "FAILED", ct); // registra el intento fallido
            return new ReserveResult(ReservationOutcome.Unavailable, 0m);
        }

        return new ReserveResult(ReservationOutcome.Reserved, await PriceOfAsync(eventId, ct));
    }

    /// <summary>
    /// Liberación idempotente (compensación, H-02 AC3): reclama RESERVED→RELEASED exactamente
    /// una vez y revierte el decremento. Duplicado ⇒ no incrementa (anti sobre-incremento).
    /// </summary>
    public async Task<ReleaseOutcome> ReleaseAsync(string eventId, string purchaseId, int seats, CancellationToken ct)
    {
        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = ReservationPrefix + purchaseId },
                },
                UpdateExpression = "SET #s = :released",
                ConditionExpression = "#s = :reserved",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "state" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":released"] = new() { S = "RELEASED" },
                    [":reserved"] = new() { S = "RESERVED" },
                },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            return ReleaseOutcome.Duplicate;
        }

        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TableName,
            Key = Key(eventId),
            UpdateExpression = "SET available = available + :n",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":n"] = new() { N = seats.ToString(CultureInfo.InvariantCulture) },
            },
        }, ct);
        return ReleaseOutcome.Released;
    }

    private async Task<string?> GetReservationStateAsync(string purchaseId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = ReservationPrefix + purchaseId },
            },
            ConsistentRead = true,
        }, ct);
        return response.Item is not { Count: > 0 } ? null : response.Item["state"].S;
    }

    private async Task MarkReservationAsync(string purchaseId, string state, CancellationToken ct) =>
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = TableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new() { S = ReservationPrefix + purchaseId },
            },
            UpdateExpression = "SET #s = :state",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "state" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":state"] = new() { S = state },
            },
        }, ct);

    private async Task<decimal> PriceOfAsync(string eventId, CancellationToken ct) =>
        (await GetByIdAsync(eventId, ct))?.Price ?? 0m;

    private static Dictionary<string, AttributeValue> Key(string id) =>
        new() { ["pk"] = new AttributeValue { S = PkPrefix + id } };

    private static Dictionary<string, AttributeValue> ToItem(CatalogEvent evt) => new()
    {
        ["pk"] = new AttributeValue { S = PkPrefix + evt.Id },
        ["name"] = new AttributeValue { S = evt.Name },
        ["date"] = new AttributeValue { S = evt.Date.ToString("O", CultureInfo.InvariantCulture) },
        ["price"] = new AttributeValue { N = evt.Price.ToString(CultureInfo.InvariantCulture) },
        ["available"] = new AttributeValue { N = evt.Available.ToString(CultureInfo.InvariantCulture) },
    };

    private static CatalogEvent FromItem(Dictionary<string, AttributeValue> item) =>
        CatalogEvent.Create(
            item["pk"].S[PkPrefix.Length..],
            item["name"].S,
            DateTimeOffset.Parse(item["date"].S, CultureInfo.InvariantCulture),
            decimal.Parse(item["price"].N, CultureInfo.InvariantCulture),
            int.Parse(item["available"].N, CultureInfo.InvariantCulture));

    private async Task EnsureTableAsync(CancellationToken ct)
    {
        try
        {
            var described = await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = TableName }, ct);
            if (described.Table.TableStatus == TableStatus.ACTIVE) return;
            if (described.Table.TableStatus != TableStatus.DELETING)
            {
                await PollStatusAsync(TableStatus.ACTIVE, ct);
                return;
            }
            await PollGoneAsync(ct); // fidelidad del emulador: ventana DELETING (hallazgo de la evidencia del TL)
        }
        catch (ResourceNotFoundException)
        {
            // No existe → crear abajo.
        }

        await ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = TableName,
            KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
            AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
        }, ct);
        await PollStatusAsync(TableStatus.ACTIVE, ct);
    }

    private async Task PollStatusAsync(TableStatus expected, CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            var described = await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = TableName }, ct);
            if (described.Table.TableStatus == expected) return;
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"La tabla {TableName} no llegó a {expected} en 20 s.");
    }

    private async Task PollGoneAsync(CancellationToken ct)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = TableName }, ct);
            }
            catch (ResourceNotFoundException)
            {
                return;
            }
            await Task.Delay(200, ct);
        }
        throw new TimeoutException($"La tabla {TableName} sigue en DELETING tras 20 s.");
    }
}
