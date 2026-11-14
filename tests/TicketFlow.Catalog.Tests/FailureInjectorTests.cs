using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// FailureInjector (D-0012, N-2002): la inyección que el evaluador opera por /admin/failure.
/// Transient consume count intentos y deja de fallar (AC1 exacto con count=2); persistent
/// falla hasta DELETE; el comodín "*" aplica a cualquier paso.
/// </summary>
public sealed class FailureInjectorTests
{
    [Fact]
    public void Transient_fails_exactly_count_times_then_stops()
    {
        var injector = new FailureInjector();
        injector.Inject("reserve-seats", FailureInjector.Transient, 2);

        Assert.NotNull(injector.TryConsume("reserve-seats")); // intento 1 falla
        Assert.NotNull(injector.TryConsume("reserve-seats")); // intento 2 falla
        Assert.Null(injector.TryConsume("reserve-seats")); // 3er intento pasa (AC1: 2 reintentos)

        Assert.Empty(injector.View()); // regla agotada: se autolimpia
    }

    [Fact]
    public void Persistent_fails_forever_until_removed()
    {
        var injector = new FailureInjector();
        injector.Inject("authorize-payment", FailureInjector.Persistent, 2);

        for (var i = 0; i < 20; i++)
            Assert.NotNull(injector.TryConsume("authorize-payment"));

        Assert.True(injector.Remove("authorize-payment"));
        Assert.Null(injector.TryConsume("authorize-payment"));
    }

    [Fact]
    public void Wildcard_applies_to_any_step_and_exact_step_wins()
    {
        var injector = new FailureInjector();
        injector.Inject("*", FailureInjector.Transient, 1);

        Assert.NotNull(injector.TryConsume("issue-tickets")); // comodín
        Assert.Null(injector.TryConsume("issue-tickets")); // agotado
    }

    [Fact]
    public void View_shows_remaining_and_null_for_persistent()
    {
        var injector = new FailureInjector();
        injector.Inject("issue-tickets", FailureInjector.Transient, 3);
        injector.Inject("reserve-seats", FailureInjector.Persistent, 2);
        injector.TryConsume("issue-tickets");

        var view = injector.View();
        Assert.Contains(("issue-tickets", FailureInjector.Transient, 2), view);
        Assert.Contains(("reserve-seats", FailureInjector.Persistent, null), view);
    }

    [Fact]
    public void Remove_unknown_step_returns_false()
    {
        Assert.False(new FailureInjector().Remove("no-existe"));
    }
}
