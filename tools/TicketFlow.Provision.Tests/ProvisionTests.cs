using TicketFlow.Catalog.Core.Infrastructure;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Payments.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;
using TicketFlow.Ticketing.Core.Infrastructure;

namespace TicketFlow.Provision.Tests;

/// <summary>Raíz del repo (donde vive TicketFlow.sln) — los tests corren desde bin/Debug.</summary>
public static class Repo
{
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TicketFlow.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repo (TicketFlow.sln).");
    }

    public static string ManifestPath => Path.Combine(Root(), "infra", "aws-local.json");
}

/// <summary>Soporte de integración (patrón de las suites existentes): sin env AWS_EMULATOR_* se omite.</summary>
public static class EmulatorTestSupport
{
    public static void SkipIfNotConfigured()
    {
        var configured =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_EMULATOR_ACCESS_KEY_ID"))
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_EMULATOR_SECRET_ACCESS_KEY"))
            && File.Exists(Environment.GetEnvironmentVariable("AWS_EMULATOR_CA_PATH") ?? "");
        if (!configured)
            Skip.If(true,
                "Emulador AWS no configurado: seteá AWS_EMULATOR_* (scripts/provision.ps1 las extrae). Integración omitida.");
    }
}

/// <summary>
/// Guard test D-0019 §4: el manifiesto debe coincidir EXACTAMENTE con las constantes de código —
/// tocar una constante (o el manifiesto) sin la otra falla acá. Referencia los 4 Core.
/// </summary>
public sealed class ManifestGuardTests
{
    [Fact]
    public void Tables_match_code_constants_and_shape()
    {
        var manifest = Manifest.Load(Repo.ManifestPath);

        var expected = new[]
        {
            DynamoDbPurchaseRepository.TableName, DynamoDbOutbox.TableName, DynamoDbIdempotencyStore.TableName,
            DynamoDbEventRepository.TableName, DynamoDbPaymentRepository.TableName, DynamoDbTicketRepository.TableName,
        };
        Assert.Equal(expected.Order(), manifest.Tables.Select(t => t.Name).Order());

        foreach (var table in manifest.Tables)
        {
            Assert.Equal("pk", Assert.Single(table.KeySchema).AttributeName);
            Assert.Equal("HASH", Assert.Single(table.KeySchema).KeyType);
            Assert.Equal("pk", Assert.Single(table.AttributeDefinitions).AttributeName);
            Assert.Equal("S", Assert.Single(table.AttributeDefinitions).AttributeType);
            Assert.Equal("PAY_PER_REQUEST", table.BillingMode);
        }
    }

    [Fact]
    public void Queues_match_code_constants_and_shape()
    {
        var manifest = Manifest.Load(Repo.ManifestPath);

        var expected = SagaQueues.All
            .Concat(SagaQueues.All.Select(SqsSagaTransport.DlqQueueOf))
            .ToList();
        Assert.Equal(expected.Order(), manifest.Queues.Select(q => q.Name).Order());

        foreach (var queue in manifest.Queues)
        {
            Assert.True(queue.Fifo, $"{queue.Name} debe declarar fifo=true");
            Assert.True(queue.ContentBasedDeduplication, $"{queue.Name} debe declarar contentBasedDeduplication=true");
        }
    }

    [Fact]
    public void Buckets_match_code_constants()
    {
        var manifest = Manifest.Load(Repo.ManifestPath);
        Assert.Equal(new[] { S3QrPayloadStore.BucketName }, manifest.Buckets.Select(b => b.Name).ToList());
    }
}

/// <summary>Validación de frontera del manifiesto (NFR-1): JSON hostil ⇒ error accionable, nunca verde silencioso.</summary>
public sealed class ManifestValidationTests
{
    [Fact]
    public void Load_rejects_missing_file() =>
        Assert.Contains("inexistente",
            Assert.Throws<InvalidManifestException>(() => Manifest.Load("no-existe.json")).Message);

    [Fact]
    public void Parse_rejects_invalid_json() =>
        Assert.Contains("JSON",
            Assert.Throws<InvalidManifestException>(() => Manifest.Parse("{ no json")).Message);

    [Theory]
    [InlineData("declara 'x' más de una vez")]
    [InlineData("sin 'queues'")]
    [InlineData("contradice el sufijo")]
    [InlineData("no tiene attributeDefinition")]
    [InlineData("keyType")]
    [InlineData("billingMode")]
    [InlineData("nombre vacío")]
    public void Parse_rejects_hostile_manifests(string caseName)
    {
        var json = HostileManifest(caseName);
        Assert.Contains(caseName,
            Assert.Throws<InvalidManifestException>(() => Manifest.Parse(json)).Message);
    }

    private static string HostileManifest(string caseName) => caseName switch
    {
        "declara 'x' más de una vez" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"},
              {"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[],"buckets":[]}
            """,
        "sin 'queues'" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[],"buckets":[{"name":"b"}]}
            """,
        "contradice el sufijo" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[{"name":"no-fifo-suffix.fifo","fifo":false,"contentBasedDeduplication":true}],"buckets":[{"name":"b"}]}
            """,
        "no tiene attributeDefinition" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[{"name":"q.fifo","fifo":true,"contentBasedDeduplication":true}],"buckets":[{"name":"b"}]}
            """,
        "keyType" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"RANGE"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[{"name":"q.fifo","fifo":true,"contentBasedDeduplication":true}],"buckets":[{"name":"b"}]}
            """,
        "billingMode" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"FREE"}],
             "queues":[{"name":"q.fifo","fifo":true,"contentBasedDeduplication":true}],"buckets":[{"name":"b"}]}
            """,
        "nombre vacío" => """
            {"tables":[{"name":"x","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[{"name":"  ","fifo":true,"contentBasedDeduplication":true}],"buckets":[{"name":"b"}]}
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(caseName), caseName, "caso no declarado"),
    };
}

/// <summary>La tool contra el emulador REAL: apply crea lo del manifiesto, verify coincide 1:1, apply es idempotente.</summary>
public sealed class ProvisionToolTests
{
    private static async Task<(int Exit, string Output)> Run(params string[] args)
    {
        using var stdout = new StringWriter();
        var exit = await Program.Run(args, stdout, CancellationToken.None);
        return (exit, stdout.ToString());
    }

    [Fact]
    public async Task Run_rejects_unknown_argument_without_touching_aws()
    {
        var (exit, output) = await Run("--nonsense");
        Assert.Equal(2, exit);
        Assert.Contains("desconocido", output);
    }

    [Fact]
    public async Task Run_rejects_missing_manifest_before_touching_aws()
    {
        var (exit, output) = await Run("--manifest", "no-existe.json");
        Assert.Equal(2, exit);
        Assert.Contains("inexistente", output);
    }

    [SkippableFact]
    public async Task Apply_then_verify_matches_manifest_one_to_one()
    {
        EmulatorTestSupport.SkipIfNotConfigured();

        var (applyExit, applyOut) = await Run("--apply", "--manifest", Repo.ManifestPath);
        Assert.Equal(0, applyExit);
        Assert.Contains("apply OK: 6 tablas · 8 colas · 1 bucket", applyOut);

        var (verifyExit, verifyOut) = await Run("--verify", "--manifest", Repo.ManifestPath);
        Assert.Equal(0, verifyExit);
        Assert.Contains("verificación OK: los recursos reales coinciden con el manifiesto.", verifyOut);
        Assert.DoesNotContain("FALTA", verifyOut);
        Assert.DoesNotContain("DIFF", verifyOut);
    }

    [SkippableFact]
    public async Task Apply_twice_more_is_idempotent()
    {
        EmulatorTestSupport.SkipIfNotConfigured();

        var first = await Run("--apply", "--manifest", Repo.ManifestPath);
        Assert.Equal(0, first.Exit);
        var second = await Run("--apply", "--manifest", Repo.ManifestPath);
        Assert.Equal(0, second.Exit);
        // Nota: el PutBucket del emulador es idempotente-sin-excepción (no lanza
        // BucketAlreadyOwnedByYou como AWS real): la prueba de no-duplicados es el verify.

        var verify = await Run("--verify", "--manifest", Repo.ManifestPath);
        Assert.Equal(0, verify.Exit);
        Assert.Contains("verificación OK", verify.Output);
    }

    [SkippableFact]
    public async Task Verify_exits_one_with_diff_when_resource_is_missing()
    {
        EmulatorTestSupport.SkipIfNotConfigured();

        // Manifiesto con un recurso inexistente en cada tipo ⇒ FALTA ×3 ⇒ exit 1 con diff visible.
        var path = Path.Combine(Path.GetTempPath(), "it-provision-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, """
            {"tables":[{"name":"it-provision-missing","keySchema":[{"attributeName":"pk","keyType":"HASH"}],
              "attributeDefinitions":[{"attributeName":"pk","attributeType":"S"}],"billingMode":"PAY_PER_REQUEST"}],
             "queues":[{"name":"it-provision-missing.fifo","fifo":true,"contentBasedDeduplication":true}],
             "buckets":[{"name":"it-provision-missing"}]}
            """);
        try
        {
            var (exit, output) = await Run("--verify", "--manifest", path);
            Assert.Equal(1, exit);
            Assert.Contains("FALTA tabla 'it-provision-missing'", output);
            Assert.Contains("FALTA cola 'it-provision-missing.fifo'", output);
            Assert.Contains("FALTA bucket 'it-provision-missing'", output);
            Assert.Contains("exit 1", output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Cognito + Parameter Store en el manifiesto (D-0024): mismo guard que tablas y colas.</summary>
public sealed class ManifestAuthGuardTests
{
    [Fact]
    public void User_pool_and_parameters_match_code_constants()
    {
        var manifest = Manifest.Load(Repo.ManifestPath);
        Assert.Equal(CognitoSetup.UserPoolName, manifest.UserPool!.Name);
        Assert.Equal(CognitoSetup.ClientName, manifest.UserPool.ClientName);
        Assert.Equal(ParameterNames.HoldTtlSeconds, Assert.Single(manifest.Parameters!).Name);
    }

    [Theory]
    [InlineData("""{"name":"otro-pool","clientName":"ticketflow-cli"}""", "[]")]
    [InlineData("null", """[{"name":"/otra-raiz/x","value":"1"}]""")]
    [InlineData("null", """[{"name":"/ticketflow/x","value":" "}]""")]
    [InlineData("null", "[null]")]
    public void Parse_rejects_unusable_auth_or_parameters(string userPool, string parameters)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Repo.ManifestPath))!;
        node["userPool"] = System.Text.Json.Nodes.JsonNode.Parse(userPool);
        node["parameters"] = System.Text.Json.Nodes.JsonNode.Parse(parameters);
        Assert.Throws<InvalidManifestException>(() => Manifest.Parse(node.ToJsonString()));
    }
}
