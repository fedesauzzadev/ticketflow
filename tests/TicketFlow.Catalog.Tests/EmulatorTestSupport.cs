namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Soporte de integración: los tests contra el emulador real (:9297) se activan con las
/// env AWS_EMULATOR_* (las extrae scripts/run-local.ps1). Sin ellas se omiten (skip
/// explícito — nunca verde silencioso).
/// </summary>
public static class EmulatorTestSupport
{
    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_EMULATOR_ACCESS_KEY_ID"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_EMULATOR_SECRET_ACCESS_KEY"))
        && File.Exists(Environment.GetEnvironmentVariable("AWS_EMULATOR_CA_PATH") ?? "");

    public static void SkipIfNotConfigured()
    {
        if (!IsConfigured)
            Skip.If(true,
                "Emulador AWS no configurado: seteá AWS_EMULATOR_ACCESS_KEY_ID / AWS_EMULATOR_SECRET_ACCESS_KEY / " +
                "AWS_EMULATOR_CA_PATH (scripts/run-local.ps1 las extrae del contenedor). Integración omitida.");
    }
}
