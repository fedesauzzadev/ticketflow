// Probe de servicios del emulador AWS local (:9297) con AWS SDK for .NET v4.
// Verifica: DynamoDB, SQS, SNS, S3 — operaciones mínimas que el MVP necesita.
using System.Security.Cryptography.X509Certificates;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
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

// Confía SOLO en la CA del emulador (pin) — no toca el trust store del SO.
var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);

// Sanity: HttpClient crudo con la CA pineada, ANTES del SDK.
{
    var handler = new HttpClientHandler();
    handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
    {
        if (cert is null || chain is null) { Console.WriteLine("  [tls] cert/chain null"); return false; }
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        var ok = chain.Build(cert);
        if (!ok)
            foreach (var s in chain.ChainStatus)
                Console.WriteLine($"  [tls] chain status: {s.Status} {s.StatusInformation.Trim()}");
        return ok;
    };
    using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    try
    {
        var body = await http.GetStringAsync($"{endpoint}/__admin/health");
        Console.WriteLine($"  [tls] raw HttpClient + CA pineada -> {body}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [tls] raw HttpClient FALLO: {ex}");
        return 3;
    }
}

var pinned = new PinnedCaFactory(ca);
try { Amazon.AWSConfigs.HttpClientFactory = pinned; } catch (Exception ex) { Console.WriteLine($"  [tls] global factory no aplicable: {ex.Message}"); }

var creds = new BasicAWSCredentials(accessKey, secretKey);
var results = new List<(string svc, string op, string outcome)>();

AmazonDynamoDBClient Ddb() => new(creds, new AmazonDynamoDBConfig
{ ServiceURL = endpoint, AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(10), HttpClientFactory = pinned });
AmazonSQSClient Sqs() => new(creds, new AmazonSQSConfig
{ ServiceURL = endpoint, AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(10), HttpClientFactory = pinned });
AmazonSimpleNotificationServiceClient Sns() => new(creds, new AmazonSimpleNotificationServiceConfig
{ ServiceURL = endpoint, AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(10), HttpClientFactory = pinned });
AmazonS3Client S3() => new(creds, new AmazonS3Config
{ ServiceURL = endpoint, AuthenticationRegion = "us-east-1", Timeout = TimeSpan.FromSeconds(10), HttpClientFactory = pinned });

async Task Try(string svc, string op, Func<Task> action)
{
    try { await action(); results.Add((svc, op, "OK")); }
    catch (Exception ex)
    {
        var msg = ex.ToString().ReplaceLineEndings(" ");
        if (msg.Length > 200) msg = msg[..200];
        results.Add((svc, op, $"FAIL [{ex.GetType().Name}] {msg}"));
    }
}

// ---- DynamoDB ----
var ddb = Ddb();
try
{
    await ddb.DeleteTableAsync("probe-table");
    for (var i = 0; i < 50; i++)
    {
        try { await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = "probe-table" }); await Task.Delay(200); }
        catch (Amazon.DynamoDBv2.Model.ResourceNotFoundException) { break; }
    }
}
catch { /* no existía */ }
await Try("dynamodb", "ListTables", async () =>
{
    var r = await ddb.ListTablesAsync();
    Console.WriteLine($"  ListTables -> [{string.Join(",", r.TableNames)}]");
});
await Try("dynamodb", "CreateTable", async () =>
{
    await ddb.CreateTableAsync(new CreateTableRequest
    {
        TableName = "probe-table",
        KeySchema = [new KeySchemaElement("pk", KeyType.HASH)],
        AttributeDefinitions = [new AttributeDefinition("pk", ScalarAttributeType.S)],
        BillingMode = BillingMode.PAY_PER_REQUEST,
    });
});
await Try("dynamodb", "DescribeTable (wait ACTIVE)", async () =>
{
    for (var i = 0; i < 20; i++)
    {
        var d = await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = "probe-table" });
        if (d.Table.TableStatus == TableStatus.ACTIVE) { Console.WriteLine("  tabla ACTIVE"); return; }
        await Task.Delay(200);
    }
    throw new Exception("tabla nunca ACTIVE");
});
await Try("dynamodb", "PutItem", async () =>
{
    await ddb.PutItemAsync(new PutItemRequest
    {
        TableName = "probe-table",
        Item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new AttributeValue { S = "probe#1" },
            ["val"] = new AttributeValue { N = "10" },
        },
    });
});
await Try("dynamodb", "GetItem", async () =>
{
    var g = await ddb.GetItemAsync(new GetItemRequest
    {
        TableName = "probe-table",
        Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "probe#1" } },
    });
    Console.WriteLine($"  GetItem val={g.Item["val"].N}");
});
await Try("dynamodb", "UpdateItem + ConditionExpression (decremento atomico)", async () =>
{
    var u = await ddb.UpdateItemAsync(new UpdateItemRequest
    {
        TableName = "probe-table",
        Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "probe#1" } },
        UpdateExpression = "SET val = val - :one",
        ConditionExpression = "val > :zero",
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":one"] = new AttributeValue { N = "1" },
            [":zero"] = new AttributeValue { N = "0" },
        },
        ReturnValues = ReturnValue.UPDATED_NEW,
    });
    Console.WriteLine($"  UpdateItem condicional -> val={u.Attributes["val"].N}");
});
await Try("dynamodb", "ConditionExpression rechaza cuando no cumple (anti-oversell)", async () =>
{
    try
    {
        await ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = "probe-table",
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new AttributeValue { S = "probe#1" } },
            UpdateExpression = "SET val = val - :one",
            ConditionExpression = "val > :max",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":one"] = new AttributeValue { N = "1" },
                [":max"] = new AttributeValue { N = "100" },
            },
        });
        throw new Exception("condicion debia fallar y NO fallo");
    }
    catch (ConditionalCheckFailedException)
    {
        Console.WriteLine("  rechazo correcto: ConditionalCheckFailedException");
    }
});

// ---- SQS ----
var sqs = Sqs();
string? queueUrl = null;
try
{
    var existing = await sqs.GetQueueUrlAsync("probe-queue.fifo");
    await sqs.DeleteQueueAsync(existing.QueueUrl);
    await Task.Delay(500);
}
catch { /* no existía */ }
await Try("sqs", "ListQueues", async () => { await sqs.ListQueuesAsync(new ListQueuesRequest()); });
await Try("sqs", "CreateQueue (.fifo + ContentBasedDeduplication)", async () =>
{
    var r = await sqs.CreateQueueAsync(new CreateQueueRequest
    {
        QueueName = "probe-queue.fifo",
        Attributes = new Dictionary<string, string> { ["FifoQueue"] = "true", ["ContentBasedDeduplication"] = "true" },
    });
    queueUrl = r.QueueUrl;
    Console.WriteLine($"  queueUrl={queueUrl}");
});
await Try("sqs", "SendMessage (fifo, dedup id)", async () =>
{
    if (queueUrl is null) throw new Exception("sin queue");
    var r = await sqs.SendMessageAsync(new SendMessageRequest(queueUrl, """{"cmd":"probe"}""")
    {
        MessageGroupId = "g1",
        MessageDeduplicationId = "dedup-1",
    });
    Console.WriteLine($"  messageId={r.MessageId}");
});
await Try("sqs", "ReceiveMessage + DeleteMessage", async () =>
{
    if (queueUrl is null) throw new Exception("sin queue");
    var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl = queueUrl,
        MaxNumberOfMessages = 10,
        WaitTimeSeconds = 2,
        MessageAttributeNames = ["All"],
    });
    Console.WriteLine($"  recibidos={r.Messages?.Count ?? 0}");
    foreach (var m in r.Messages ?? []) await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
});

// ---- SNS ----
var sns = Sns();
string? topicArn = null;
try { var t = await sns.FindTopicAsync("probe-topic.fifo"); if (t is not null) await sns.DeleteTopicAsync(t.TopicArn); await Task.Delay(300); } catch { /* no existía */ }
await Try("sns", "ListTopics", async () => { await sns.ListTopicsAsync(new ListTopicsRequest()); });
await Try("sns", "CreateTopic (.fifo)", async () =>
{
    var r = await sns.CreateTopicAsync(new CreateTopicRequest
    {
        Name = "probe-topic.fifo",
        Attributes = new Dictionary<string, string> { ["FifoTopic"] = "true" },
    });
    topicArn = r.TopicArn;
    Console.WriteLine($"  topicArn={topicArn}");
});
await Try("sns", "Subscribe (protocolo sqs, queue ARN)", async () =>
{
    if (topicArn is null || queueUrl is null) throw new Exception("sin topic/queue");
    var attrs = await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
    {
        QueueUrl = queueUrl,
        AttributeNames = ["QueueArn"],
    });
    var queueArn = attrs.Attributes["QueueArn"];
    Console.WriteLine($"  queueArn={queueArn}");
    var r = await sns.SubscribeAsync(new SubscribeRequest { TopicArn = topicArn, Protocol = "sqs", Endpoint = queueArn });
    Console.WriteLine($"  subscription={r.SubscriptionArn}");
});
await Try("sns", "Publish (fifo)", async () =>
{
    if (topicArn is null) throw new Exception("sin topic");
    var r = await sns.PublishAsync(new PublishRequest
    {
        TopicArn = topicArn,
        Message = """{"event":"probe"}""",
        MessageGroupId = "g1",
        MessageDeduplicationId = "dedup-publish-1",
    });
    Console.WriteLine($"  messageId={r.MessageId}");
});
await Try("sns->sqs", "fanout: ReceiveMessage en cola suscrita", async () =>
{
    if (queueUrl is null) throw new Exception("sin queue");
    await Task.Delay(500);
    var r = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl = queueUrl,
        MaxNumberOfMessages = 10,
        WaitTimeSeconds = 2,
    });
    Console.WriteLine($"  mensajes fanout recibidos={r.Messages?.Count ?? 0}");
    if ((r.Messages?.Count ?? 0) == 0) throw new Exception("fanout SNS->SQS no entrego ningun mensaje");
    foreach (var m in r.Messages ?? []) await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle);
});

// ---- S3 ----
var s3 = S3();
await Try("s3", "ListBuckets", async () =>
{
    var r = await s3.ListBucketsAsync();
    var names = r.Buckets?.Select(b => b.BucketName).ToList() ?? [];
    Console.WriteLine($"  buckets=[{string.Join(",", names)}]");
});
await Try("s3", "PutBucket", async () => { await s3.PutBucketAsync(new PutBucketRequest { BucketName = "probe-bucket" }); });
await Try("s3", "PutObject", async () =>
{
    await s3.PutObjectAsync(new PutObjectRequest
    { BucketName = "probe-bucket", Key = "tickets/probe.txt", ContentBody = "QR-PROBE-123" });
});
await Try("s3", "GetObject", async () =>
{
    var g = await s3.GetObjectAsync(new GetObjectRequest { BucketName = "probe-bucket", Key = "tickets/probe.txt" });
    using var sr = new StreamReader(g.ResponseStream);
    Console.WriteLine($"  body={await sr.ReadToEndAsync()}");
});

// ---- Resumen ----
Console.WriteLine();
Console.WriteLine("==== RESUMEN ====");
foreach (var (svc, op, outcome) in results)
    Console.WriteLine($"{svc,-10} {op,-48} {outcome}");
var fails = results.Count(r => r.outcome.StartsWith("FAIL"));
Console.WriteLine($"\n{results.Count - fails}/{results.Count} OK, {fails} FAIL");
return fails == 0 ? 0 : 1;

public class PinnedCaFactory(X509Certificate2 ca) : Amazon.Runtime.HttpClientFactory
{
    public override HttpClient CreateHttpClient(Amazon.Runtime.IClientConfig _)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
        {
            if (cert is null || chain is null) return false;
            // Pin directo: el root servido debe SER la CA del emulador. Tambien evita
            // llamadas silenciosas a AWS real si la config de endpoint se rompe.
            var root = chain.ChainElements[^1].Certificate;
            return string.Equals(root.Thumbprint, ca.Thumbprint, StringComparison.OrdinalIgnoreCase);
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
