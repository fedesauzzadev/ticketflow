using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Api;

/// <summary>
/// Sweeper de holds (D-0017 §2): BackgroundService de Órdenes, intervalo FIJO de 5 s (sin
/// config — anti-flexibilidad). Por pasada: Scan de vencibles → CAS SEATS_RESERVED→EXPIRED →
/// outbox release-seats SOLO si el CAS gana. Stateless: el estado vive en DynamoDB del emulador,
/// por eso la expiración sobrevive reinicios de los microservicios (AC7). El tier de contingencia
/// (§5) reclama claims zombis (payment_initiated_at &lt; now − 2×TTL − 30 s) para que TODA compra
/// termine terminal con motivo.
/// </summary>
public sealed class HoldExpirySweeper(
    IPurchaseRepository purchases,
    IOutbox outbox,
    int holdTtlSeconds,
    ILogger<HoldExpirySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "sweeper de holds activo: intervalo 5 s, TTL {Ttl} s, tier de contingencia tras {Zombie} s",
            holdTtlSeconds, 2 * holdTtlSeconds + 30);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "iteración del sweeper de holds falló; reintenta en 5 s");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Una pasada determinista (now inyectado — testeable sin reloj real). Devuelve cuántos
    /// holds expiró efectivamente (los que perdieron el CAS por un claim no cuentan).
    /// </summary>
    public async Task<int> SweepOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        var nowEpoch = now.ToUnixTimeSeconds();
        var zombieCutoff = nowEpoch - (2 * holdTtlSeconds + 30); // gracia documentada (D-0017 §5)
        var candidates = await purchases.ScanExpirableHoldsAsync(nowEpoch, zombieCutoff, ct);

        var expired = 0;
        foreach (var candidate in candidates)
        {
            var isZombie = candidate.PaymentInitiatedAt is not null;
            var ttlSeconds = candidate.ExpiresAt is { } exp
                ? Math.Max(0, (int)(exp - candidate.CreatedAt.ToUnixTimeSeconds()))
                : holdTtlSeconds;
            var reason = isZombie
                ? $"pago iniciado hace más de {2 * holdTtlSeconds + 30} s sin completarse — " +
                  "reserva reclamada por contingencia (el pago puede haberse aprobado después: " +
                  "verificar en Pagos antes de reintentar)"
                : $"reserva expirada sin pago (TTL {ttlSeconds} s)";
            var won = isZombie
                ? await purchases.TryExpireZombieAsync(candidate.PurchaseId, reason, zombieCutoff, ct)
                : await purchases.TryExpireAsync(candidate.PurchaseId, reason, ct);
            if (!won) continue; // un claim ganó la carrera entre scan y CAS: la saga sigue (§4)

            // Liberación idempotente por construcción (D-0017 §3): decrement_pids traga la
            // segunda liberación si una compensación ya había soltado el asiento.
            await outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReleaseSeats,
                candidate.PurchaseId, new ReleaseSeatsCommand(candidate.EventId, candidate.Seats), ct);
            logger.LogInformation(
                "compra {PurchaseId}: paso 'hold-expired' → EXPIRED ({Reason}); emito release-seats",
                candidate.PurchaseId, reason);
            expired++;
        }
        return expired;
    }
}
