using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Orders.Api;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Orders.Tests;

/// <summary>
/// Tests unitarios del mecanismo de holds (D-0017/D-0018) — sin red: fake CAS + reloj fijo.
/// Cubren la rama hold del process manager, los guards de eventos tardíos sobre EXPIRED, la
/// validación fail-fast de HOLD_TTL_SECONDS, el sweeper determinista (now inyectado) y el
/// property test de tormenta de eventos con semilla fija (invariantes del slice).
/// </summary>
public sealed class HoldMechanicsTests
{
    private sealed class RecordingOutbox : IOutbox
    {
        public List<(string Queue, string Type, string PurchaseId, object Payload)> Enqueued { get; } = [];

        public Task EnqueueAsync(string queueName, string type, string purchaseId, object payload,
            CancellationToken ct)
        {
            Enqueued.Add((queueName, type, purchaseId, payload));
            return Task.CompletedTask;
        }
    }

    private static SagaEnvelope Env<T>(string type, string purchaseId, T payload) =>
        new(type, purchaseId, JsonSerializer.SerializeToElement(payload, SagaJson.Options));

    private static (SagaProcessManager Manager, FakePurchaseRepository Purchases, RecordingOutbox Outbox)
        NewMechanics(int ttl = 90)
    {
        var purchases = new FakePurchaseRepository();
        var outbox = new RecordingOutbox();
        return (new SagaProcessManager(purchases, outbox,
            NullLogger<SagaProcessManager>.Instance, ttl), purchases, outbox);
    }

    private static void StartHold(FakePurchaseRepository purchases, string id, int ttl,
        DateTimeOffset? now = null)
    {
        var epoch = (now ?? purchases.Now).ToUnixTimeSeconds();
        purchases.Store[id] = Purchase.Create(id, "evt-test", 2, "4111111111111111", "key-1",
            purchases.Now, hold: true);
        purchases.TryHoldReserveAsync(id, epoch + ttl, 200m, CancellationToken.None).GetAwaiter().GetResult();
    }

    // --- Rama hold del process manager (D-0018 §1) --------------------------------

    [Fact]
    public async Task Hold_seats_reserved_writes_expiry_and_amount_and_defers_payment()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = NewMechanics(ttl: 90);
        purchases.Store[id] = Purchase.Create(id, "evt-test", 2, "4111111111111111", "key-1",
            purchases.Now, hold: true);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); // el PM usa el reloj real

        await manager.HandleAsync(
            Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 100m)), CancellationToken.None);

        var hold = purchases.Store[id];
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, hold.Status); // no-terminal esperando el pago
        Assert.InRange(hold.ExpiresAt!.Value, before + 90, before + 91); // TTL aplicado desde la reserva
        Assert.Equal(200m, hold.Amount); // monto persistido para el pago diferido
        Assert.Empty(outbox.Enqueued); // AC-opuesto-de-V1: NO se emite authorize-payment
    }

    [Fact]
    public async Task Non_hold_seats_reserved_still_emits_authorize_payment_immediately()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = NewMechanics();
        purchases.Store[id] = Purchase.Create(id, "evt-test", 1, "4111111111111111", "key-1",
            purchases.Now);

        await manager.HandleAsync(
            Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 50m)), CancellationToken.None);

        Assert.Equal(PurchaseStatus.SEATS_RESERVED, purchases.Store[id].Status);
        var command = Assert.IsType<AuthorizePaymentCommand>(Assert.Single(outbox.Enqueued).Payload);
        Assert.Equal(50m, command.Amount); // camino V1 intacto (regresión prohibida)
    }

    [Fact]
    public async Task Hold_claimed_in_time_completes_saga_like_V1()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = NewMechanics(ttl: 90);
        StartHold(purchases, id, ttl: 90);

        // El endpoint de pago gana el claim (D-0017 §4) y emite authorize-payment como V1.
        Assert.Equal(PaymentClaimOutcome.Claimed,
            await purchases.TryClaimPaymentAsync(id, CancellationToken.None));
        var held = purchases.Store[id];
        await outbox.EnqueueAsync(SagaQueues.PaymentsCommands, SagaTypes.AuthorizePayment, id,
            new AuthorizePaymentCommand(held.Pan, held.Amount!.Value), CancellationToken.None);

        await manager.HandleAsync(
            Env(SagaTypes.PaymentApproved, id, new PaymentApprovedEvent(200m)), CancellationToken.None);
        await manager.HandleAsync(
            Env(SagaTypes.TicketsIssued, id, new TicketsIssuedEvent(["tkt-1", "tkt-2"])), CancellationToken.None);

        Assert.Equal(PurchaseStatus.TICKETS_ISSUED, purchases.Store[id].Status); // AC3
        // La saga procedió aunque el sweeper ya podría querer expirarla: el claim la protege.
        Assert.False(await purchases.TryExpireAsync(id, "intento tardío", CancellationToken.None));
    }

    // --- Guards de eventos tardíos sobre EXPIRED (D-0018 §5) ----------------------

    [Fact]
    public async Task Payment_approved_after_expiry_is_ignored_without_commands()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = NewMechanics();
        StartHold(purchases, id, ttl: 1);
        purchases.Now += TimeSpan.FromSeconds(5); // venció
        Assert.True(await purchases.TryExpireAsync(id, "reserva expirada sin pago (TTL 1s)",
            CancellationToken.None));

        await manager.HandleAsync(
            Env(SagaTypes.PaymentApproved, id, new PaymentApprovedEvent(200m)), CancellationToken.None);

        Assert.Equal(PurchaseStatus.EXPIRED, purchases.Store[id].Status); // terminal no muta
        Assert.Empty(outbox.Enqueued); // sin issue-tickets, sin liberaciones extra
    }

    [Fact]
    public async Task Payment_declined_after_expiry_emits_no_release()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = NewMechanics();
        StartHold(purchases, id, ttl: 1);
        purchases.Now += TimeSpan.FromSeconds(5);
        await purchases.TryExpireAsync(id, "reserva expirada sin pago (TTL 1s)", CancellationToken.None);

        await manager.HandleAsync(
            Env(SagaTypes.PaymentDeclined, id, new PaymentDeclinedEvent("tarjeta rechazada")), CancellationToken.None);

        Assert.Equal(PurchaseStatus.EXPIRED, purchases.Store[id].Status);
        Assert.Empty(outbox.Enqueued); // el asiento ya volvió por la expiración: sin segunda liberación (AC6)
    }

    [Fact]
    public async Task Seats_released_and_step_failed_after_expiry_keep_terminal_expired()
    {
        var id = PurchaseIds.New();
        var (manager, purchases, outbox) = NewMechanics();
        StartHold(purchases, id, ttl: 1);
        purchases.Now += TimeSpan.FromSeconds(5);
        await purchases.TryExpireAsync(id, "reserva expirada sin pago (TTL 1s)", CancellationToken.None);

        await manager.HandleAsync(Env(SagaTypes.SeatsReleased, id, new SeatsReleasedEvent("evt-test")),
            CancellationToken.None);
        await manager.HandleAsync(
            Env(SagaTypes.StepFailed, id, new StepFailedEvent(SagaTypes.AuthorizePayment, "timeout")),
            CancellationToken.None);

        Assert.Equal(PurchaseStatus.EXPIRED, purchases.Store[id].Status); // F-V1-01: lo terminal no se pisa
        Assert.Empty(outbox.Enqueued);
    }

    // --- HOLD_TTL_SECONDS fail-fast (D-0017 §6, NFR-1) -----------------------------

    [Fact]
    public void HoldTtl_defaults_and_ranges()
    {
        Assert.Equal(90, HoldTtl.DefaultSeconds);
        try
        {
            Environment.SetEnvironmentVariable("HOLD_TTL_SECONDS", null);
            Assert.Equal(90, HoldTtl.FromEnvironment());
            Environment.SetEnvironmentVariable("HOLD_TTL_SECONDS", "  ");
            Assert.Equal(90, HoldTtl.FromEnvironment());
            Environment.SetEnvironmentVariable("HOLD_TTL_SECONDS", "300");
            Assert.Equal(300, HoldTtl.FromEnvironment());
            foreach (var invalid in new[] { "4", "3601", "abc", "-1" })
            {
                Environment.SetEnvironmentVariable("HOLD_TTL_SECONDS", invalid);
                Assert.Throws<InvalidOperationException>(() => HoldTtl.FromEnvironment()); // fail-fast
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOLD_TTL_SECONDS", null);
        }
    }

    // --- Sweeper determinista (D-0017 §2/§4/§5) ------------------------------------

    private static (HoldExpirySweeper Sweeper, FakePurchaseRepository Purchases, RecordingOutbox Outbox)
        NewSweeper(int ttl = 90)
    {
        var purchases = new FakePurchaseRepository();
        var outbox = new RecordingOutbox();
        return (new HoldExpirySweeper(purchases, outbox, ttl,
            NullLogger<HoldExpirySweeper>.Instance), purchases, outbox);
    }

    [Fact]
    public async Task Sweep_expires_due_hold_once_and_enqueues_single_release()
    {
        var (sweeper, purchases, outbox) = NewSweeper(ttl: 5);
        var id = PurchaseIds.New();
        StartHold(purchases, id, ttl: 5); // expira 5 s después de Now
        var now = purchases.Now + TimeSpan.FromSeconds(6);

        Assert.Equal(1, await sweeper.SweepOnceAsync(now, CancellationToken.None));
        var expired = purchases.Store[id];
        Assert.Equal(PurchaseStatus.EXPIRED, expired.Status);
        Assert.Contains("reserva expirada sin pago (TTL 5 s)", expired.FailureReason);
        var release = Assert.IsType<ReleaseSeatsCommand>(Assert.Single(outbox.Enqueued).Payload);
        Assert.Equal(("evt-test", 2), (release.EventId, release.Seats));

        Assert.Equal(0, await sweeper.SweepOnceAsync(now + TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Single(outbox.Enqueued); // ya EXPIRED: el scan no la vuelve a traer
    }

    [Fact]
    public async Task Sweep_leaves_fresh_and_claimed_holds_alone()
    {
        var (sweeper, purchases, outbox) = NewSweeper(ttl: 60);
        var fresh = PurchaseIds.New();
        var claimed = PurchaseIds.New();
        StartHold(purchases, fresh, ttl: 60);
        StartHold(purchases, claimed, ttl: 60);
        Assert.Equal(PaymentClaimOutcome.Claimed,
            await purchases.TryClaimPaymentAsync(claimed, CancellationToken.None));
        purchases.Now += TimeSpan.FromSeconds(30); // dentro del TTL

        Assert.Equal(0, await sweeper.SweepOnceAsync(purchases.Now, CancellationToken.None));
        Assert.All(purchases.Store.Values, p => Assert.Equal(PurchaseStatus.SEATS_RESERVED, p.Status));
        Assert.Empty(outbox.Enqueued); // D-0017 §4: el claim protege la saga en vuelo
    }

    [Fact]
    public async Task Sweep_reclaims_zombie_claim_with_contingency_reason()
    {
        var (sweeper, purchases, outbox) = NewSweeper(ttl: 5);
        var id = PurchaseIds.New();
        StartHold(purchases, id, ttl: 5);
        Assert.Equal(PaymentClaimOutcome.Claimed,
            await purchases.TryClaimPaymentAsync(id, CancellationToken.None));
        // El claim quedó zombi: 2×TTL+30 s (gracia §5) sin completar el pago.
        var now = purchases.Now + TimeSpan.FromSeconds(2 * 5 + 31);

        Assert.Equal(1, await sweeper.SweepOnceAsync(now, CancellationToken.None));
        var expired = purchases.Store[id];
        Assert.Equal(PurchaseStatus.EXPIRED, expired.Status);
        Assert.Contains("contingencia", expired.FailureReason); // terminal con motivo (F-V1-01)
        // F-V2-05: la ventana zombi deja un pago APPROVED tardío sin ticket — el reason
        // advierte verificar Pagos antes de reintentar la compra.
        Assert.Contains("verificar en Pagos", expired.FailureReason);
        Assert.Single(outbox.Enqueued); // release-seats: el asiento vuelve al pool
    }

    [Fact]
    public async Task Sweep_ignores_non_hold_purchases_entirely()
    {
        var (sweeper, purchases, outbox) = NewSweeper();
        var id = PurchaseIds.New();
        purchases.Store[id] = Purchase.Create(id, "evt-test", 1, "4111111111111111", "k", purchases.Now);
        await purchases.TryTransitionAsync(id, PurchaseStatus.PENDING, PurchaseStatus.SEATS_RESERVED,
            ct: CancellationToken.None); // estado transitorio del happy path V1

        Assert.Equal(0, await sweeper.SweepOnceAsync(purchases.Now + TimeSpan.FromHours(1),
            CancellationToken.None));
        Assert.Equal(PurchaseStatus.SEATS_RESERVED, purchases.Store[id].Status); // sin expires_at ⇒ invisible
        Assert.Empty(outbox.Enqueued);
    }

    // --- Property test: tormenta de eventos con semilla fija (invariantes H-09) ----
    // Modela el EFECTO del catálogo (D-0017 §3): la liberación idempotente por decrement_pids
    // incrementa la disponibilidad UNA sola vez aunque lleguen dos comandos release-seats
    // (expiración + compensación, o tier zombi §5). La invariante observable es el efecto,
    // no la emisión.

    [Fact]
    public async Task Randomized_hold_event_storm_preserves_invariants()
    {
        const int seed = 20261115; // semilla fija: reproducible
        var rng = new Random(seed);
        for (var scenario = 0; scenario < 80; scenario++)
        {
            var ttl = 5 + rng.Next(20);
            var purchases = new FakePurchaseRepository();
            var outbox = new RecordingOutbox();
            var manager = new SagaProcessManager(purchases, outbox,
                NullLogger<SagaProcessManager>.Instance, ttl);
            var sweeper = new HoldExpirySweeper(purchases, outbox, ttl,
                NullLogger<HoldExpirySweeper>.Instance);

            var id = PurchaseIds.New();
            var hold = rng.Next(2) == 0;
            purchases.Store[id] = Purchase.Create(id, "evt-test", 1, "4111111111111111", "k",
                purchases.Now, hold);
            var available = 9; // modelo del catálogo para ESTE asiento
            await manager.HandleAsync(
                Env(SagaTypes.SeatsReserved, id, new SeatsReservedEvent("evt-test", 80m)),
                CancellationToken.None);
            available--; // la reserva decrementa una vez (idempotente por decrement_pids)
            var seatReleased = false; // decrement_pids: el efecto de liberar pasa UNA vez
            var claimWon = false;
            var everExpired = false;
            var claimAfterExpiryClaimed = false;
            void ReleaseEffect() // cada release-seats emitido toca el catálogo idempotente
            {
                if (seatReleased) return;
                seatReleased = true;
                available++;
            }

            foreach (var op in Enumerable.Range(0, 1 + rng.Next(8)).Select(_ => rng.Next(7)))
            {
                switch (op)
                {
                    case 0: // el evaluador reclama el pago (antes o después del TTL)
                        if (everExpired && purchases.Store[id].Status == PurchaseStatus.EXPIRED)
                            claimAfterExpiryClaimed |= await purchases.TryClaimPaymentAsync(id,
                                CancellationToken.None) == PaymentClaimOutcome.Claimed;
                        else if (await purchases.TryClaimPaymentAsync(id, CancellationToken.None)
                                 == PaymentClaimOutcome.Claimed)
                        {
                            claimWon = true;
                            await outbox.EnqueueAsync(SagaQueues.PaymentsCommands,
                                SagaTypes.AuthorizePayment, id,
                                new AuthorizePaymentCommand("4111111111111111", 80m),
                                CancellationToken.None);
                        }
                        break;
                    case 1: // una pasada del sweeper con el reloj corrido al azar (incluye tier zombi)
                        purchases.Now += TimeSpan.FromSeconds(rng.Next(0, ttl * 3));
                        await sweeper.SweepOnceAsync(purchases.Now, CancellationToken.None);
                        everExpired |= purchases.Store[id].Status == PurchaseStatus.EXPIRED;
                        break;
                    case 2: // payment-approved: alcanzable cuando no es hold o el claim ya voló
                        if (!hold || claimWon)
                            await manager.HandleAsync(Env(SagaTypes.PaymentApproved, id,
                                new PaymentApprovedEvent(80m)), CancellationToken.None);
                        break;
                    case 3: // payment-declined: compensación con el mismo guard de alcanzabilidad
                        if (!hold || claimWon)
                            await manager.HandleAsync(Env(SagaTypes.PaymentDeclined, id,
                                new PaymentDeclinedEvent("tarjeta rechazada")), CancellationToken.None);
                        break;
                    case 4: // duplicado de seats-reserved (redelivery)
                        await manager.HandleAsync(Env(SagaTypes.SeatsReserved, id,
                            new SeatsReservedEvent("evt-test", 80m)), CancellationToken.None);
                        break;
                    case 5:
                        await manager.HandleAsync(Env(SagaTypes.TicketsIssued, id,
                            new TicketsIssuedEvent(["tkt-x"])), CancellationToken.None);
                        break;
                    case 6: // ack de la compensación llegando en cualquier orden
                        await manager.HandleAsync(Env(SagaTypes.SeatsReleased, id,
                            new SeatsReleasedEvent("evt-test")), CancellationToken.None);
                        break;
                }
                foreach (var _ in outbox.Enqueued.Where(e => e.Type == SagaTypes.ReleaseSeats))
                    ReleaseEffect(); // el relay publica TODO lo enqueued al catálogo idempotente
            }

            var final = purchases.Store[id];

            // I1 (AC6, H-07): expiración y compensación nunca acumulan liberaciones — la
            // disponibilidad vive en {inicial−1, inicial} pase lo que pase la tormenta.
            Assert.True(available is 8 or 9, $"escenario {scenario}: available={available} (hold={hold})");
            // I2 (AC2): si el asiento volvió al pool, volvió UNA vez (available == 9 ⇒ reservado otra vez imposible aquí).
            Assert.True(!seatReleased || available == 9, $"escenario {scenario}: liberado más de una vez");
            // I3 (AC4/F-V1-01): EXPIRED es terminal — ninguna tormenta lo revierte.
            Assert.True(!everExpired || final.Status == PurchaseStatus.EXPIRED,
                $"escenario {scenario}: expirada terminó en {final.Status}");
            // I4 (D-0017 §4): un pago NUNCA prospera sobre una compra ya expirada.
            Assert.False(claimAfterExpiryClaimed, $"escenario {scenario}: claim ganó post-expiración");
            // I5: ticket solo si la saga completó con pago aprobado.
            Assert.True(final.TicketIds is null || final.Status == PurchaseStatus.TICKETS_ISSUED,
                $"escenario {scenario}: tickets en estado {final.Status}");
        }
    }
}
