using Amazon.CognitoIdentityProvider;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SQS;
using Amazon.SQS.Model;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Provision;

/// <summary>
/// Tool IaC del emulador (D-0019): aplica/verifica el manifiesto infra/aws-local.json reusando la
/// plomería verificada de Shared.Infrastructure (ensure tolerante a carreras L-007 ⇒ idempotencia
/// por construcción). Modos: --apply (default, exit 0) · --verify (falta/difiere vs manifiesto ⇒
/// exit 1 con el diff; extras ⇒ warning, exit 0 — sesión compartida por diseño). Otros errores: 2.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args) =>
        await Run(args, Console.Out, CancellationToken.None);

    public static async Task<int> Run(IReadOnlyList<string> args, TextWriter stdout, CancellationToken ct)
    {
        var verify = false;
        string? tokenUser = null;
        var manifestPath = "infra/aws-local.json";
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--apply": verify = false; break;
                case "--verify": verify = true; break;
                case "--token":
                    if (i + 1 >= args.Count) { Usage(stdout, "--token requiere un nombre de usuario."); return 2; }
                    tokenUser = args[++i]; break;
                case "--manifest":
                    if (i + 1 >= args.Count) { Usage(stdout, "--manifest requiere la ruta del manifiesto."); return 2; }
                    manifestPath = args[++i]; break;
                case "-h" or "--help":
                    Usage(stdout, null);
                    return 0;
                default:
                    Usage(stdout, $"argumento desconocido: '{args[i]}'.");
                    return 2;
            }
        }

        Manifest manifest;
        try
        {
            manifest = Manifest.Load(manifestPath);
        }
        catch (InvalidManifestException ex)
        {
            stdout.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }

        try
        {
            var options = EmulatorAwsOptions.FromEnvironment();
            using var ddb = EmulatorAwsClients.CreateDynamoDb(options);
            using var sqs = EmulatorAwsClients.CreateSqs(options);
            using var s3 = EmulatorAwsClients.CreateS3(options);
            using var ssm = EmulatorAwsClients.CreateSsm(options);
            using var cognito = EmulatorAwsClients.CreateCognito(options);
            if (tokenUser is not null)
            {
                // Solo el token en stdout: los scripts lo capturan tal cual.
                stdout.WriteLine((await CognitoSetup.SignInDemoUserAsync(cognito, ssm, tokenUser, ct)).AccessToken);
                return 0;
            }
            if (!verify)
            {
                var applied = await ApplyAsync(manifest, ddb, sqs, s3, stdout, ct);
                await ApplyAuthAndParametersAsync(manifest, cognito, ssm, stdout, ct);
                return applied;
            }
            var missing = await VerifyAuthAndParametersAsync(manifest, cognito, ssm, stdout, ct);
            var exit = await VerifyAsync(manifest, ddb, sqs, s3, stdout, ct);
            if (missing > 0) stdout.WriteLine($"verificación FALLÓ: {missing} diferencia(s) en Cognito/Parameter Store.");
            return missing > 0 ? 1 : exit;
        }
        catch (Exception ex)
        {
            stdout.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }
    }

    private static void Usage(TextWriter o, string? error)
    {
        if (error is not null) o.WriteLine($"ERROR: {error}");
        o.WriteLine("Uso: TicketFlow.Provision [--apply | --verify | --token <usuario>] [--manifest <ruta>]");
        o.WriteLine("  --apply   (default) asegura todos los recursos del manifiesto (idempotente).");
        o.WriteLine("  --verify  lista los recursos reales y los compara con el manifiesto: falta o");
        o.WriteLine("            difiere => exit 1 con el diff; extras => warning (exit 0).");
        o.WriteLine("  --token   imprime un access token de Cognito para un usuario de demo (solo emulador).");
        o.WriteLine("  Requiere env AWS_EMULATOR_* (scripts/provision.ps1 la inyecta).");
    }

    // --- Cognito + Parameter Store (D-0024) ------------------------------------------

    private static async Task ApplyAuthAndParametersAsync(Manifest manifest, IAmazonCognitoIdentityProvider cognito,
        IAmazonSimpleSystemsManagement ssm, TextWriter o, CancellationToken ct)
    {
        if (manifest.UserPool is { } pool)
        {
            var (poolId, _) = await CognitoSetup.EnsureAsync(cognito, ssm, ct);
            o.WriteLine($"cognito {pool.Name} OK ({poolId})");
        }
        foreach (var p in manifest.Parameters ?? [])
        {
            await CognitoSetup.PutAsync(ssm, p.Name, p.Value, overwrite: false, ct); // respeta cambios hechos a mano
            o.WriteLine($"param   {p.Name} OK");
        }
    }

    private static async Task<int> VerifyAuthAndParametersAsync(Manifest manifest,
        IAmazonCognitoIdentityProvider cognito, IAmazonSimpleSystemsManagement ssm, TextWriter o, CancellationToken ct)
    {
        if (manifest.UserPool is null && manifest.Parameters is null) return 0;
        var problems = 0;
        var real = await ParameterStore.LoadAsync(ssm, ct);
        void Check(bool ok, string what)
        {
            o.WriteLine($"    {(ok ? "OK   " : "FALTA")} {what}");
            if (!ok) problems++;
        }
        o.WriteLine("==> Cognito y Parameter Store:");
        if (manifest.UserPool is { } pool)
        {
            var poolId = await CognitoSetup.FindPoolIdAsync(cognito, ct);
            var clientId = poolId is null ? null : await CognitoSetup.FindClientIdAsync(cognito, poolId, ct);
            Check(poolId is not null, $"user pool '{pool.Name}'");
            Check(clientId is not null, $"app client '{pool.ClientName}'");
            Check(poolId is not null && real.GetValueOrDefault(ParameterNames.UserPoolId) == poolId,
                $"parámetro '{ParameterNames.UserPoolId}' apunta al pool");
            Check(clientId is not null && real.GetValueOrDefault(ParameterNames.ClientId) == clientId,
                $"parámetro '{ParameterNames.ClientId}' apunta al client");
        }
        foreach (var p in manifest.Parameters ?? [])
            Check(real.ContainsKey(p.Name), $"parámetro '{p.Name}'");
        return problems;
    }

    // --- --apply ---------------------------------------------------------------------

    private static async Task<int> ApplyAsync(
        Manifest manifest, IAmazonDynamoDB ddb, IAmazonSQS sqs, IAmazonS3 s3, TextWriter o, CancellationToken ct)
    {
        foreach (var table in manifest.Tables)
        {
            await DynamoDbTables.EnsureAsync(ddb, table.Name, ct);
            o.WriteLine($"tabla   {table.Name} OK");
        }

        // El ensure del transporte crea FIFO+CDB: el manifiesto solo puede declarar eso (D-0019 §1).
        var transport = new SqsSagaTransport(sqs);
        foreach (var queue in manifest.Queues)
        {
            if (!queue.Fifo || !queue.ContentBasedDeduplication)
                throw new NotSupportedException(
                    $"Cola '{queue.Name}': solo colas FIFO con contentBasedDeduplication=true " +
                    "están soportadas (D-0019 §1 — el ensure de la saga crea exactamente eso).");
            await transport.EnsureQueueAsync(queue.Name, ct);
            o.WriteLine($"cola    {queue.Name} OK");
        }

        foreach (var bucket in manifest.Buckets)
            await EnsureBucketAsync(s3, bucket.Name, o, ct);

        o.WriteLine($"apply OK: {manifest.Tables.Count} tablas · {manifest.Queues.Count} colas · " +
                    $"{manifest.Buckets.Count} bucket(s) asegurados (idempotente).");
        return 0;
    }

    /// <summary>Patrón idempotente del bucket (mismo que S3QrPayloadStore: put, ya-existe OK, transient reintenta).</summary>
    private static async Task EnsureBucketAsync(IAmazonS3 s3, string name, TextWriter o, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await s3.PutBucketAsync(new PutBucketRequest { BucketName = name }, ct);
                o.WriteLine($"bucket  {name} OK");
                return;
            }
            catch (BucketAlreadyOwnedByYouException)
            {
                o.WriteLine($"bucket  {name} OK (ya existía)");
                return;
            }
            catch (AmazonServiceException)
            {
                await Task.Delay(250, ct); // carrera/transient del emulador: reintentar
            }
        }
        throw new TimeoutException($"El bucket {name} no quedó disponible en ~2,5 s.");
    }

    // --- --verify --------------------------------------------------------------------

    private static async Task<int> VerifyAsync(
        Manifest manifest, IAmazonDynamoDB ddb, IAmazonSQS sqs, IAmazonS3 s3, TextWriter o, CancellationToken ct)
    {
        var problems = 0;

        // Tablas: presencia + forma (keySchema, atributos, billing) contra DescribeTable.
        var realTables = await ListTablesAsync(ddb, ct);
        o.WriteLine($"==> Tablas ({manifest.Tables.Count} en manifiesto, {realTables.Count} reales):");
        foreach (var table in manifest.Tables)
        {
            if (!realTables.Contains(table.Name))
            {
                o.WriteLine($"    FALTA tabla '{table.Name}'");
                problems++;
                continue;
            }
            var described = (await ddb.DescribeTableAsync(new DescribeTableRequest { TableName = table.Name }, ct)).Table;
            var diff = TableDiff(table, described);
            if (diff is null)
                o.WriteLine($"    OK    {table.Name} ({described.KeySchema[0].AttributeName}:HASH " +
                            $"{described.AttributeDefinitions[0].AttributeType}, {described.BillingModeSummary?.BillingMode ?? "PROVISIONED"})");
            else
            {
                o.WriteLine($"    DIFF  tabla '{table.Name}': {diff}");
                problems++;
            }
        }
        problems += WarnExtras(o, "tabla", manifest.Tables.Select(t => t.Name).ToList(), realTables);

        // Colas: presencia + atributos FIFO/CDB contra GetQueueAttributes.
        var realQueues = new Dictionary<string, string>(); // name -> url
        string? next = null;
        do
        {
            var page = await sqs.ListQueuesAsync(new ListQueuesRequest { NextToken = next }, ct);
            next = page.NextToken;
            foreach (var url in page.QueueUrls ?? [])
                realQueues[Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..])] = url;
        } while (next is not null);

        o.WriteLine($"==> Colas ({manifest.Queues.Count} en manifiesto, {realQueues.Count} reales):");
        foreach (var queue in manifest.Queues)
        {
            if (!realQueues.TryGetValue(queue.Name, out var url))
            {
                o.WriteLine($"    FALTA cola '{queue.Name}'");
                problems++;
                continue;
            }
            var attrs = (await sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest
            {
                QueueUrl = url,
                AttributeNames = ["FifoQueue", "ContentBasedDeduplication"],
            }, ct)).Attributes;
            var fifo = attrs.TryGetValue("FifoQueue", out var f) && f == "true";
            var cdb = attrs.TryGetValue("ContentBasedDeduplication", out var c) && c == "true";
            if (fifo == queue.Fifo && cdb == queue.ContentBasedDeduplication)
                o.WriteLine($"    OK    {queue.Name} (fifo={fifo}, contentBasedDeduplication={cdb})");
            else
            {
                o.WriteLine($"    DIFF  cola '{queue.Name}': real fifo={fifo}/cdb={cdb} ≠ " +
                            $"manifiesto fifo={queue.Fifo}/cdb={queue.ContentBasedDeduplication}");
                problems++;
            }
        }
        problems += WarnExtras(o, "cola", manifest.Queues.Select(q => q.Name).ToList(), realQueues.Keys.ToList());

        // Buckets: presencia (no tienen forma). El emulador devuelve Buckets=null con 0 buckets
        // (F-V2-03): null-guard — lista vacía => los del manifiesto se reportan FALTA (exit 1).
        var realBuckets = ((await s3.ListBucketsAsync(ct)).Buckets ?? [])
            .Select(b => b.BucketName).ToList();
        o.WriteLine($"==> Buckets ({manifest.Buckets.Count} en manifiesto, {realBuckets.Count} reales):");
        foreach (var bucket in manifest.Buckets)
        {
            if (realBuckets.Contains(bucket.Name))
                o.WriteLine($"    OK    {bucket.Name}");
            else
            {
                o.WriteLine($"    FALTA bucket '{bucket.Name}'");
                problems++;
            }
        }
        problems += WarnExtras(o, "bucket", manifest.Buckets.Select(b => b.Name).ToList(), realBuckets);

        if (problems == 0)
        {
            o.WriteLine("verificación OK: los recursos reales coinciden con el manifiesto.");
            return 0;
        }
        o.WriteLine($"verificación FALLÓ: {problems} diferencia(s) contra el manifiesto (exit 1).");
        return 1;
    }

    private static async Task<List<string>> ListTablesAsync(IAmazonDynamoDB ddb, CancellationToken ct)
    {
        var names = new List<string>();
        string? last = null;
        do
        {
            var page = await ddb.ListTablesAsync(new ListTablesRequest { ExclusiveStartTableName = last }, ct);
            names.AddRange(page.TableNames);
            last = page.LastEvaluatedTableName;
        } while (!string.IsNullOrEmpty(last));
        return names;
    }

    private static string? TableDiff(ManifestTable expected, TableDescription real)
    {
        var expectedKeys = string.Join(",", expected.KeySchema.Select(k => $"{k.AttributeName}:{k.KeyType}"));
        var realKeys = string.Join(",", real.KeySchema.Select(k => $"{k.AttributeName}:{k.KeyType}"));
        if (expectedKeys != realKeys) return $"keySchema real [{realKeys}] ≠ manifiesto [{expectedKeys}]";

        var expectedAttrs = string.Join(",", expected.AttributeDefinitions
            .Select(a => $"{a.AttributeName}:{a.AttributeType}").Order());
        var realAttrs = string.Join(",", real.AttributeDefinitions
            .Select(a => $"{a.AttributeName}:{a.AttributeType}").Order());
        if (expectedAttrs != realAttrs) return $"atributos reales [{realAttrs}] ≠ manifiesto [{expectedAttrs}]";

        var realBilling = real.BillingModeSummary?.BillingMode ?? "PROVISIONED";
        if (!string.Equals(expected.BillingMode, realBilling, StringComparison.OrdinalIgnoreCase))
            return $"billingMode real [{realBilling}] ≠ manifiesto [{expected.BillingMode}]";

        return null;
    }

    /// <summary>Extras ⇒ WARNING (exit 0): sobre sesión compartida (tests it-*, probes) es por diseño (D-0019 §2).</summary>
    private static int WarnExtras(TextWriter o, string kind, IReadOnlyList<string> declared, List<string> real)
    {
        var extras = real.Where(r => !declared.Contains(r)).ToList();
        foreach (var extra in extras)
            o.WriteLine($"    WARN  {kind} extra '{extra}' (no está en el manifiesto)");
        return 0;
    }
}
