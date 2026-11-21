namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// Configuración del emulador AWS local — 100% por variables de entorno, cero secrets en el repo (D-0003).
/// Las credenciales las extrae scripts/run-local.ps1 del contenedor (se regeneran por corrida del emulador).
/// </summary>
public sealed class EmulatorAwsOptions
{
    public string Endpoint { get; init; } = "https://127.0.0.1:9297";
    public string Region { get; init; } = "us-east-1";
    public string AccessKeyId { get; init; } = "";
    public string SecretAccessKey { get; init; } = "";
    public string CaPath { get; init; } = "";

    /// Timeout explícito de toda IO al emulador (NFR-3).
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Lee y valida la config al arranque (fail-fast, NFR-1). Además del formato, valida el
    /// gate de división (NFR-7): endpoint solo loopback — junto al pin de CA hace imposible
    /// apuntar por accidente a AWS real.
    /// </summary>
    public static EmulatorAwsOptions FromEnvironment()
    {
        var options = new EmulatorAwsOptions
        {
            Endpoint = Environment.GetEnvironmentVariable("AWS_EMULATOR_ENDPOINT") ?? "https://127.0.0.1:9297",
            Region = Environment.GetEnvironmentVariable("AWS_EMULATOR_REGION") ?? "us-east-1",
            AccessKeyId = Environment.GetEnvironmentVariable("AWS_EMULATOR_ACCESS_KEY_ID") ?? "",
            SecretAccessKey = Environment.GetEnvironmentVariable("AWS_EMULATOR_SECRET_ACCESS_KEY") ?? "",
            CaPath = Environment.GetEnvironmentVariable("AWS_EMULATOR_CA_PATH") ?? "",
        };

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.AccessKeyId)) missing.Add("AWS_EMULATOR_ACCESS_KEY_ID");
        if (string.IsNullOrWhiteSpace(options.SecretAccessKey)) missing.Add("AWS_EMULATOR_SECRET_ACCESS_KEY");
        if (string.IsNullOrWhiteSpace(options.CaPath)) missing.Add("AWS_EMULATOR_CA_PATH");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"Faltan variables de entorno del emulador: {string.Join(", ", missing)}. " +
                "Ejecutá scripts/run-local.ps1 — extrae credenciales y CA del contenedor y las inyecta por env.");

        if (!File.Exists(options.CaPath))
            throw new InvalidOperationException($"AWS_EMULATOR_CA_PATH apunta a un archivo inexistente: '{options.CaPath}'.");

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"AWS_EMULATOR_ENDPOINT debe ser una URL HTTPS: '{options.Endpoint}'.");

        if (!IsAllowedHost(uri.Host, Environment.GetEnvironmentVariable("AWS_EMULATOR_ALLOWED_HOST")))
            throw new InvalidOperationException(
                $"AWS_EMULATOR_ENDPOINT debe ser loopback (el emulador corre local): '{options.Endpoint}'. " +
                "El resto de hosts se rechaza por diseño: cero llamadas a nube real (NFR-7). " +
                "En contenedores, AWS_EMULATOR_ALLOWED_HOST autoriza UN host extra (nunca *.amazonaws.com).");

        return options;
    }

    /// <summary>
    /// Gate NFR-7: loopback siempre; además UN host extra opt-in (D-0023) para cuando el
    /// servicio corre en un contenedor y el emulador es otro host de la red (compose/k8s).
    /// Un dominio de AWS real no se puede autorizar; el pin de CA sigue siendo la garantía dura.
    /// </summary>
    public static bool IsAllowedHost(string host, string? allowedHost)
    {
        if (host is "127.0.0.1" or "localhost" or "::1" or "[::1]") return true;
        if (string.IsNullOrWhiteSpace(allowedHost)) return false;
        if (allowedHost.Contains("amazonaws", StringComparison.OrdinalIgnoreCase) ||
            allowedHost.Contains("aws.com", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(host, allowedHost.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
