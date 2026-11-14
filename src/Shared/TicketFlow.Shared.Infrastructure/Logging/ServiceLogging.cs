using Microsoft.Extensions.Logging;

namespace TicketFlow.Shared.Infrastructure.Logging;

/// <summary>
/// Contrato de logging de los servicios (D-0011): JSON consola + Timestamp ISO-8601
/// round-trip en UTC. El timestamp de ancho fijo "O" en un único reloj (UTC) hace que
/// el orden lexicográfico de las líneas de los 4 servicios sea el orden cronológico
/// real — es lo que ordena scripts/trace-purchase.ps1. El correlation-id (PurchaseId)
/// viaja como propiedad del mensaje en cada paso de la saga (sweep S05).
/// </summary>
public static class ServiceLogging
{
    public static ILoggingBuilder AddServiceTraceLogging(this ILoggingBuilder builder) =>
        builder.AddJsonConsole(options =>
        {
            // ISO-8601 de ancho fijo con Z literal ("O" sobre DateTimeOffset emite "+00:00"):
            // p.ej. 2026-11-14T21:04:05.1234567Z — orden lexicográfico == orden cronológico.
            options.TimestampFormat = @"yyyy-MM-ddTHH:mm:ss.fffffff'Z'";
            options.UseUtcTimestamp = true; // un solo reloj para los 4 servicios.
        });
}
