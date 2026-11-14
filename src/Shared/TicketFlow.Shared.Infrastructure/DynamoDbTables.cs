using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;

namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// Creación idempotente y TOLERANTE A CARRERAS de tablas DynamoDB del emulador (D-0005). Los 4
/// servicios arrancan a la vez (run-local) y varios pueden intentar crear la misma tabla/cola:
/// el emulador devuelve 500 crudos en esas carreras — aquí se absorben re-describiendo hasta
/// ACTIVE (deadline explícito). Maneja la ventana DELETING verificada por el TL.
/// </summary>
public static class DynamoDbTables
{
    public static async Task EnsureAsync(IAmazonDynamoDB ddb, string tableName, CancellationToken ct)
    {
        // ~30 s de presupuesto (120 × 250 ms): creación + polling a ACTIVE.
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                var described = await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = tableName }, ct);
                if (described.Table.TableStatus == TableStatus.ACTIVE) return;
                // CREATING/DELETING/UPDATING: cae al create de abajo (ResourceInUse) y se re-describe.
            }
            catch (ResourceNotFoundException)
            {
                // No existe → crear abajo.
            }
            catch (AmazonServiceException)
            {
                await Task.Delay(250, ct); // transient del emulador (carrera de arranque)
                continue;
            }

            try
            {
                await ddb.CreateTableAsync(new CreateTableRequest
                {
                    TableName = tableName,
                    KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
                    AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
                    BillingMode = BillingMode.PAY_PER_REQUEST,
                }, ct);
            }
            catch (Exception ex) when (ex is ResourceInUseException or AmazonServiceException)
            {
                // Otro proceso la está creando: el loop vuelve a describir hasta ACTIVE.
            }
            await Task.Delay(250, ct);
        }
        throw new TimeoutException($"La tabla {tableName} no llegó a ACTIVE en 30 s.");
    }
}
