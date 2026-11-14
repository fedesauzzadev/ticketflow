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
    // D-0013 (F-05): el decremento y su marca de idempotencia viven en el MISMO UpdateItem
    // del ítem del evento (ADD availability + ADD decrement_pids bajo NOT contains) — la
    // ventana claim→decremento que podía crashear a mitad NO existe por construcción.

    private const string ReservationPrefix = "RESERVATION#";
    private const string DecrementPids = "decrement_pids";

    /// <summary>
    /// Reserva idempotente de asientos (comando reserve-seats, D-0013): UN solo UpdateItem
    /// atómico "ADD availability -n, ADD decrement_pids {pid}" con condición
    /// "availability >= n AND NOT contains(decrement_pids, pid)". La condición falla ⇒ esta
    /// compra ya decrementó (redelivery tras crash: ack SIN segundo decremento) o no hay
    /// disponibilidad (guard anti-oversell de D-0005). El claim RESERVATION#pid queda como
    /// estado de negocio para la compensación.
    /// </summary>
    public async Task<ReserveResult> ReserveAsync(string eventId, string purchaseId, int seats, CancellationToken ct)
    {
        bool decremented;
        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(eventId),
                UpdateExpression = "ADD available :neg, #dp :pidset",
                // attribute_not_exists: en un evento fresco el set no existe aún — el emulador
                // resuelve contains() sobre atributo ausente como fallo (hallazgo S06).
                // contains() compara contra el :pid ESCALAR; el ADD del set usa :pidset (SS).
                ConditionExpression =
                    "available >= :n AND (attribute_not_exists(#dp) OR NOT contains(#dp, :pid))",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#dp"] = DecrementPids },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":neg"] = new() { N = (-seats).ToString(CultureInfo.InvariantCulture) },
                    [":pidset"] = new() { SS = [purchaseId] },
                    [":pid"] = new() { S = purchaseId },
                    [":n"] = new() { N = seats.ToString(CultureInfo.InvariantCulture) },
                },
            }, ct);
            decremented = true;
        }
        catch (ConditionalCheckFailedException)
        {
            decremented = false;
        }

        if (decremented)
        {
            // Claim de negocio, incondicional: el decremento atómico YA garantizó el efecto
            // único. Si un intento previo sin disponibilidad dejó state=FAILED y su mensaje
            // nunca se ackó (crash), esta entrega legítima reabre la reserva.
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = ReservationItem(eventId, purchaseId, seats, "RESERVED"),
            }, ct);
            return new ReserveResult(ReservationOutcome.Reserved, await PriceOfAsync(eventId, ct));
        }

        if (await AlreadyDecrementedAsync(eventId, purchaseId, ct))
        {
            // Redelivery tras crash entre decremento y claim (o comando duplicado): ack SIN
            // segundo decremento; se asegura el claim RESERVED para que la compensación libere.
            try
            {
                await ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = TableName,
                    Item = ReservationItem(eventId, purchaseId, seats, "RESERVED"),
                    ConditionExpression = "attribute_not_exists(pk)",
                }, ct);
            }
            catch (ConditionalCheckFailedException)
            {
                // El claim ya existía (RESERVED/RELEASED): se conserva.
            }
            return new ReserveResult(ReservationOutcome.DuplicateReserved, await PriceOfAsync(eventId, ct));
        }

        // Sin disponibilidad (o evento inexistente): registra el intento fallido, sin mutar.
        try
        {
            await ddb.PutItemAsync(new PutItemRequest
            {
                TableName = TableName,
                Item = ReservationItem(eventId, purchaseId, seats, "FAILED"),
                ConditionExpression = "attribute_not_exists(pk)",
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Ya había claim: se conserva el estado previo.
        }
        return new ReserveResult(ReservationOutcome.Unavailable, 0m);
    }

    /// <summary>
    /// Liberación idempotente (compensación, H-02 AC3, D-0013): simétrica a la reserva — UN
    /// solo UpdateItem "ADD availability +n DELETE decrement_pids {pid}" bajo contains(pid):
    /// incrementa y habilita re-reservar exactamente una vez. Duplicado (o nunca decrementado)
    /// ⇒ condición falla ⇒ sin efecto. El claim RESERVED→RELEASED va después: un crash entre
    /// ambos no reintroduce drift (el contains ya sacó el pid).
    /// </summary>
    public async Task<ReleaseOutcome> ReleaseAsync(string eventId, string purchaseId, int seats, CancellationToken ct)
    {
        try
        {
            await ddb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = TableName,
                Key = Key(eventId),
                UpdateExpression = "ADD available :pos DELETE #dp :pidset",
                ConditionExpression = "contains(#dp, :pid)",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#dp"] = DecrementPids },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pos"] = new() { N = seats.ToString(CultureInfo.InvariantCulture) },
                    [":pidset"] = new() { SS = [purchaseId] },
                    [":pid"] = new() { S = purchaseId },
                },
            }, ct);
        }
        catch (ConditionalCheckFailedException)
        {
            // Ya liberado (pid fuera del set) o nunca decrementado: sin efecto.
        }

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
            return ReleaseOutcome.Released;
        }
        catch (ConditionalCheckFailedException)
        {
            return ReleaseOutcome.Duplicate;
        }
    }

    /// <summary>Lectura consistente del set de decrementos: ¿esta compra ya decrementó? (distinguir duplicado de sin-disponibilidad).</summary>
    private async Task<bool> AlreadyDecrementedAsync(string eventId, string purchaseId, CancellationToken ct)
    {
        var response = await ddb.GetItemAsync(new GetItemRequest
        {
            TableName = TableName,
            Key = Key(eventId),
            ConsistentRead = true,
            ProjectionExpression = "#dp",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#dp"] = DecrementPids },
        }, ct);
        // L-004: el emulador devuelve Item=null (no dict vacío) cuando la key no existe.
        return response.Item?.GetValueOrDefault(DecrementPids)?.SS.Contains(purchaseId) == true;
    }

    private static Dictionary<string, AttributeValue> ReservationItem(
        string eventId, string purchaseId, int seats, string state) => new()
    {
        ["pk"] = new AttributeValue { S = ReservationPrefix + purchaseId },
        ["event_id"] = new AttributeValue { S = eventId },
        ["seats"] = new AttributeValue { N = seats.ToString(CultureInfo.InvariantCulture) },
        ["state"] = new AttributeValue { S = state },
    };

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
