using System.Text.Json;

namespace TicketFlow.Provision;

/// <summary>Manifiesto inválido (D-0019): el JSON no declara lo que la tool necesita — fail-fast (NFR-1).</summary>
public sealed class InvalidManifestException(string message) : Exception(message);

public sealed record KeyAttributeElement(string AttributeName, string KeyType);

public sealed record AttributeElement(string AttributeName, string AttributeType);

public sealed record ManifestTable(
    string Name, IReadOnlyList<KeyAttributeElement> KeySchema,
    IReadOnlyList<AttributeElement> AttributeDefinitions, string BillingMode);

public sealed record ManifestQueue(string Name, bool Fifo, bool ContentBasedDeduplication);

public sealed record ManifestBucket(string Name);

/// <summary>
/// Manifiesto IaC del emulador (D-0019): infra/aws-local.json como fuente de verdad de la LISTA de
/// recursos (AC6). Clavado contra las constantes de código por tools/TicketFlow.Provision.Tests.
/// </summary>
public sealed record Manifest(
    IReadOnlyList<ManifestTable> Tables,
    IReadOnlyList<ManifestQueue> Queues,
    IReadOnlyList<ManifestBucket> Buckets)
{
    private static readonly JsonSerializerOptions Json =
        new() { PropertyNameCaseInsensitive = true };

    public static Manifest Load(string path) =>
        File.Exists(path)
            ? Parse(File.ReadAllText(path))
            : throw new InvalidManifestException($"Manifiesto inexistente: '{path}'.");

    public static Manifest Parse(string json)
    {
        Manifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(json, Json)
                ?? throw new InvalidManifestException("Manifiesto vacío.");
        }
        catch (JsonException ex)
        {
            throw new InvalidManifestException($"Manifiesto no es JSON válido: {ex.Message}");
        }
        manifest.Validate();
        return manifest;
    }

    /// <summary>Validación de frontera (NFR-1): todo campo declarado, único y consistente con las reglas AWS.</summary>
    private void Validate()
    {
        foreach (var (kind, names) in new[]
                 {
                     ("tables", Tables.Select(t => t.Name).ToList()),
                     ("queues", Queues.Select(q => q.Name).ToList()),
                     ("buckets", Buckets.Select(b => b.Name).ToList()),
                 })
        {
            if (names.Count == 0) throw new InvalidManifestException($"Manifiesto sin '{kind}'.");
            if (names.Any(string.IsNullOrWhiteSpace))
                throw new InvalidManifestException($"'{kind}' con nombre vacío.");
            var dup = names.GroupBy(n => n).FirstOrDefault(g => g.Count() > 1);
            if (dup is not null)
                throw new InvalidManifestException($"'{kind}' declara '{dup.Key}' más de una vez.");
        }

        foreach (var table in Tables)
        {
            if (table.KeySchema.Count == 0)
                throw new InvalidManifestException($"Tabla '{table.Name}' sin keySchema.");
            var hash = table.KeySchema.Count(k => k.KeyType == "HASH");
            if (hash != 1)
                throw new InvalidManifestException(
                    $"Tabla '{table.Name}' debe declarar exactamente un keyType HASH (tiene {hash}).");
            foreach (var key in table.KeySchema)
            {
                if (string.IsNullOrWhiteSpace(key.AttributeName))
                    throw new InvalidManifestException($"Tabla '{table.Name}' con attributeName vacío en keySchema.");
                if (key.KeyType is not ("HASH" or "RANGE"))
                    throw new InvalidManifestException(
                        $"Tabla '{table.Name}': keyType '{key.KeyType}' inválido (HASH|RANGE).");
                var def = table.AttributeDefinitions.FirstOrDefault(a => a.AttributeName == key.AttributeName);
                if (def is null)
                    throw new InvalidManifestException(
                        $"Tabla '{table.Name}': el atributo de clave '{key.AttributeName}' no tiene attributeDefinition.");
                if (def.AttributeType is not ("S" or "N" or "B"))
                    throw new InvalidManifestException(
                        $"Tabla '{table.Name}': attributeType '{def.AttributeType}' inválido (S|N|B).");
            }
            if (table.BillingMode is not ("PAY_PER_REQUEST" or "PROVISIONED"))
                throw new InvalidManifestException(
                    $"Tabla '{table.Name}': billingMode '{table.BillingMode}' inválido (PAY_PER_REQUEST|PROVISIONED).");
        }

        foreach (var queue in Queues)
        {
            var endsFifo = queue.Name.EndsWith(".fifo", StringComparison.Ordinal);
            if (queue.Fifo != endsFifo)
                throw new InvalidManifestException(
                    $"Cola '{queue.Name}': fifo={queue.Fifo} contradice el sufijo del nombre (.fifo).");
        }
    }
}
