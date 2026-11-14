namespace TicketFlow.Ticketing.Tests;

/// <summary>Soporte de integración contra el emulador real (idéntico al de Catalog/Orders).</summary>
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
