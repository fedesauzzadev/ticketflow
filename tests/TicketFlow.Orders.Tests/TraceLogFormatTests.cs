using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using TicketFlow.Shared.Infrastructure.Logging;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Contrato de la traza distribuida (D-0011) que consume scripts/trace-purchase.ps1:
/// cada línea de log de los 4 servicios es JSON con Timestamp ISO-8601 UTC de formato
/// "O" (orden lexicográfico == orden cronológico), Category, Message y la propiedad
/// PurchaseId (correlation-id == purchase_id) como texto plano de la línea. Si este
/// contrato rompe, la traza unificada rompe — por eso se clava con unit contra el
/// formatter JSON REAL que registra AddServiceTraceLogging (vía DI, sin tocar Console).
/// </summary>
public class TraceLogFormatTests
{
    private static (ConsoleFormatter Formatter, IOptionsMonitor<JsonConsoleFormatterOptions> Options) Resolve()
    {
        var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddServiceTraceLogging())
            .BuildServiceProvider();
        var formatter = provider.GetServices<ConsoleFormatter>()
            .Single(f => f.Name == ConsoleFormatterNames.Json);
        return (formatter, provider.GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>());
    }

    private static string WriteStep(ConsoleFormatter formatter)
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("{OriginalFormat}", "compra {PurchaseId}: paso '{Step}'"),
            new("PurchaseId", "pur-trace-contract"),
            new("Step", "seats-reserved"),
        };
        var writer = new StringWriter();
        formatter.Write(
            new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
                LogLevel.Information, "TicketFlow.Orders.Core.Application.SagaProcessManager", default,
                state, null, (s, _) => "compra pur-trace-contract: paso 'seats-reserved'"),
            scopeProvider: null, textWriter: writer);
        return writer.ToString().Trim();
    }

    [Fact]
    public void AddServiceTraceLogging_registra_timestamp_iso_utc_de_ancho_fijo()
    {
        var (_, options) = Resolve();

        Assert.Equal(@"yyyy-MM-ddTHH:mm:ss.fffffff'Z'", options.CurrentValue.TimestampFormat);
        Assert.True(options.CurrentValue.UseUtcTimestamp);
    }

    [Fact]
    public void Linea_de_traza_es_json_con_timestamp_ordinal_utc_y_purchase_id()
    {
        var (formatter, _) = Resolve();
        var raw = WriteStep(formatter);

        using var json = JsonDocument.Parse(raw); // la línea ES JSON válido
        var root = json.RootElement;

        Assert.Equal("TicketFlow.Orders.Core.Application.SagaProcessManager", root.GetProperty("Category").GetString());
        Assert.Contains("seats-reserved", root.GetProperty("Message").GetString());

        var timestamp = root.GetProperty("Timestamp").GetString();
        Assert.NotNull(timestamp);
        Assert.EndsWith("Z", timestamp); // UTC: un solo reloj para los 4 servicios
        Assert.True(DateTime.TryParseExact(timestamp, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed) && parsed.Kind == DateTimeKind.Utc,
            $"Timestamp '{timestamp}' no es ISO-8601 round-trip: el sort de trace-purchase.ps1 dejaría de ser cronológico.");

        // El correlation-id viaja en la línea como texto (el script filtra por substring).
        Assert.Contains("pur-trace-contract", raw);
    }

    [Fact]
    public void Dos_lineas_consecutivas_ordenan_lexicograficamente_como_cronologicamente()
    {
        var (formatter, _) = Resolve();

        var first = JsonDocument.Parse(WriteStep(formatter)).RootElement.GetProperty("Timestamp").GetString()!;
        var second = JsonDocument.Parse(WriteStep(formatter)).RootElement.GetProperty("Timestamp").GetString()!;

        Assert.True(string.CompareOrdinal(first, second) <= 0,
            $"'{first}' > '{second}': el orden lexicográfico divergiría del cronológico y la traza unificada saldría desordenada.");
    }
}
