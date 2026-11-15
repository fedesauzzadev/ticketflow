// Probe V2 (N-3003): capacidades del emulador que el mecanismo de expiracion de holds necesita.
//  P1: CreateQueue FIFO + atributo DelaySeconds a nivel de COLA (en SQS real: invalido para FIFO)
//  P2: SendMessage a FIFO + DelaySeconds POR MENSAJE (en SQS real: invalido para FIFO)
//      -> si lo acepta: lo HONRA? (receive antes / despues de la ventana de delay)
//  P3: control — cola ESTANDAR + DelaySeconds por mensaje (valido en SQS real): el emulador
//      implementa el mecanismo de delay en general?
//  P4: DynamoDB Scan + FilterExpression sobre atributo AUSENTE en algunos items (el filtro que
//      usaria un sweeper: status = SEATS_RESERVED AND expires_at < now, con items sin expires_at)
using System.Security.Cryptography.X509Certificates;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

var endpoint = "https://127.0.0.1:9297";
var accessKey = Environment.GetEnvironmentVariable("EMU_AWS_ACCESS_KEY_ID") ?? "";
var secretKey = Environment.GetEnvironmentVariable("EMU_AWS_SECRET_ACCESS_KEY") ?? "";
var caPath = Environment.GetEnvironmentVariable("EMU_CA_PATH") ?? "";
if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(caPath))
{
    Console.WriteLine("FALTAN env: EMU_AWS_ACCESS_KEY_ID / EMU_AWS_SECRET_ACCESS_KEY / EMU_CA_PATH");
    return 2;
}

var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
var pinned = new PinnedCaFactory(ca);
var creds = new BasicAWSCredentials(accessKey, secretKey);
var suffix = DateTime.UtcNow.ToString("MMddHHmmss");

AmazonSQSClient Sqs() => new(creds, new AmazonSQSConfig
{ ServiceURL = endpoint, AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(10), HttpClientFactory = pinned });
AmazonDynamoDBClient Ddb() => new(creds, new AmazonDynamoDBConfig
{ ServiceURL = endpoint, AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(10), HttpClientFactory = pinned });

var results = new List<(string id, string outcome)>();
void Note(string id, string outcome)
{
    results.Add((id, outcome));
    Console.WriteLine($"[{id}] {outcome}");
}
string OneLine(Exception ex)
{
    var msg = ex.ToString().ReplaceLineEndings(" ");
    return msg.Length > 260 ? msg[..260] : msg;
}
async Task CleanupQueue(AmazonSQSClient sqs, string name)
{
    try { var u = await sqs.GetQueueUrlAsync(name); await sqs.DeleteQueueAsync(u.QueueUrl); }
    catch { /* no existia */ }
}

// ---------- P1: FIFO, DelaySeconds a nivel de cola ----------
var sqs = Sqs();
var q1 = $"probe-ttl-attr-{suffix}.fifo";
try
{
    await sqs.CreateQueueAsync(new CreateQueueRequest
    {
        QueueName = q1,
        Attributes = new Dictionary<string, string>
        {
            ["FifoQueue"] = "true",
            ["ContentBasedDeduplication"] = "true",
            ["DelaySeconds"] = "3",
        },
    });
    Note("P1", "ACEPTADO: CreateQueue FIFO con atributo DelaySeconds=3 (SQS real lo rechaza para FIFO)");
    await CleanupQueue(sqs, q1);
}
catch (Exception ex)
{
    Note("P1", $"RECHAZADO [{ex.GetType().Name}] {OneLine(ex)}");
}

// ---------- P2: FIFO, DelaySeconds por mensaje ----------
var q2 = $"probe-ttl-msg-{suffix}.fifo";
string? q2Url = null;
try
{
    var created = await sqs.CreateQueueAsync(new CreateQueueRequest
    {
        QueueName = q2,
        Attributes = new Dictionary<string, string>
        { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
    });
    q2Url = created.QueueUrl;
    await sqs.SendMessageAsync(new SendMessageRequest(q2Url, """{"probe":"ttl"}""")
    {
        MessageGroupId = "g1",
        MessageDeduplicationId = "probe-ttl-1",
        DelaySeconds = 4,
    });
    Note("P2", "ACEPTADO: SendMessage FIFO con DelaySeconds=4 (SQS real lo rechaza para FIFO)");

    // Lo honra? receive inmediato (espera corta 1 s) -> deberia estar INVISIBLE si honra el delay.
    var before = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    { QueueUrl = q2Url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
    var nBefore = before.Messages?.Count ?? 0;

    await Task.Delay(5500); // delay 4 s + margen
    var nAfter = 0;
    for (var i = 0; i < 3 && nAfter == 0; i++)
    {
        var after = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        { QueueUrl = q2Url, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
        nAfter = after.Messages?.Count ?? 0;
    }

    Note("P2-honra", nBefore == 0 && nAfter >= 1
        ? $"HONRADO: invisible antes de la ventana (recibidos={nBefore}) y visible despues ({nAfter})"
        : nBefore >= 1
            ? $"IGNORADO: el mensaje fue visible INMEDIATAMENTE (antes={nBefore}, despues={nAfter}) — acepta el parametro pero no aplica delay"
            : $"NO ENTREGADO: antes={nBefore}, despues={nAfter} — el mensaje no aparecio ni tras la ventana");
}
catch (Exception ex)
{
    Note("P2", $"RECHAZADO [{ex.GetType().Name}] {OneLine(ex)}");
}

// ---------- P3: control, cola ESTANDAR + DelaySeconds por mensaje (valido en SQS real) ----------
var q3 = $"probe-ttl-std-{suffix}";
try
{
    var created = await sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = q3 });
    var url3 = created.QueueUrl;
    await sqs.SendMessageAsync(new SendMessageRequest(url3, """{"probe":"std"}""") { DelaySeconds = 3 });
    var before = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    { QueueUrl = url3, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
    var nBefore = before.Messages?.Count ?? 0;
    await Task.Delay(4500);
    var nAfter = 0;
    for (var i = 0; i < 3 && nAfter == 0; i++)
    {
        var after = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        { QueueUrl = url3, MaxNumberOfMessages = 10, WaitTimeSeconds = 1 });
        nAfter = after.Messages?.Count ?? 0;
    }
    Note("P3", nBefore == 0 && nAfter >= 1
        ? $"HONRADO en estandar (antes={nBefore}, despues={nAfter}): el emulador SI implementa delay por mensaje"
        : $"NO honrado en estandar (antes={nBefore}, despues={nAfter})");
    await CleanupQueue(sqs, q3);
}
catch (Exception ex)
{
    Note("P3", $"ERROR [{ex.GetType().Name}] {OneLine(ex)}");
}

if (q2Url is not null) await CleanupQueue(sqs, q2);

// ---------- P4: Scan + FilterExpression con atributo ausente ----------
var ddb = Ddb();
var table = $"probe-ttl-scan-{suffix}";
try
{
    await ddb.CreateTableAsync(new CreateTableRequest
    {
        TableName = table,
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        BillingMode = BillingMode.PAY_PER_REQUEST,
    });
    for (var i = 0; i < 30; i++)
    {
        try
        {
            var d = await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table });
            if (d.Table.TableStatus == TableStatus.ACTIVE) break;
        }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException) { }
        await Task.Delay(200);
    }

    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var expired = now - 10;
    await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = new Dictionary<string, AttributeValue>
    {
        ["pk"] = new() { S = "S#1" }, ["st"] = new() { S = "SEATS_RESERVED" },
        ["expires_at"] = new() { N = expired.ToString() },
    } });
    await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = new Dictionary<string, AttributeValue>
    {
        ["pk"] = new() { S = "S#2" }, ["st"] = new() { S = "SEATS_RESERVED" }, // SIN expires_at (compra sin hold)
    } });
    await ddb.PutItemAsync(new PutItemRequest { TableName = table, Item = new Dictionary<string, AttributeValue>
    {
        ["pk"] = new() { S = "S#3" }, ["st"] = new() { S = "TICKETS_ISSUED" },
        ["expires_at"] = new() { N = expired.ToString() }, // terminal con expires_at viejo: no debe matchear
    } });

    try
    {
        var scan = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "#st = :st AND expires_at < :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "st" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":st"] = new() { S = "SEATS_RESERVED" },
                [":now"] = new() { N = now.ToString() },
            },
        });
        var got = string.Join(",", scan.Items.Select(i => i["pk"].S).OrderBy(x => x));
        Note("P4a", scan.Items.Count == 1 && got == "S#1"
            ? $"OK exacto: Scan filtro st=SEATS_RESERVED AND expires_at<now con atributo AUSENTE en otros items -> [{got}] (esperado [S#1])"
            : $"RESULTADO INESPERADO: devolvio [{got}] (esperado [S#1])");
    }
    catch (Exception ex)
    {
        Note("P4a", $"ERROR [{ex.GetType().Name}] {OneLine(ex)} — el filtro sobre atributo ausente explota");
    }

    try
    {
        var scan2 = await ddb.ScanAsync(new ScanRequest
        {
            TableName = table,
            ConsistentRead = true,
            FilterExpression = "#st = :st AND attribute_exists(expires_at) AND expires_at < :now",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#st"] = "st" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":st"] = new() { S = "SEATS_RESERVED" },
                [":now"] = new() { N = now.ToString() },
            },
        });
        var got2 = string.Join(",", scan2.Items.Select(i => i["pk"].S).OrderBy(x => x));
        Note("P4b", scan2.Items.Count == 1 && got2 == "S#1"
            ? $"OK exacto: variante attribute_exists + expires_at<now -> [{got2}]"
            : $"RESULTADO INESPERADO: [{got2}] (esperado [S#1])");
    }
    catch (Exception ex)
    {
        Note("P4b", $"ERROR [{ex.GetType().Name}] {OneLine(ex)}");
    }

    await ddb.DeleteTableAsync(table);
}
catch (Exception ex)
{
    Note("P4", $"ERROR de setup [{ex.GetType().Name}] {OneLine(ex)}");
}

Console.WriteLine();
Console.WriteLine("==== RESUMEN PROBE TTL V2 ====");
foreach (var (id, outcome) in results)
    Console.WriteLine($"{id,-8} {outcome}");
return 0;

public class PinnedCaFactory(X509Certificate2 ca) : Amazon.Runtime.HttpClientFactory
{
    public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig _)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
        {
            if (cert is null || chain is null) return false;
            var root = chain.ChainElements[^1].Certificate;
            return string.Equals(root.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase);
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
