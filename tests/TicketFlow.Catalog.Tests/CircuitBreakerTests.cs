using System.Net;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Catalog.Tests;

/// <summary>
/// Circuit breaker propio (D-0012, N-2002 AC4): máquina de estados determinista — sin reloj
/// real ni red. StubHandler cuenta cuántas veces se TOCÓ el downstream: en OPEN el fail-fast
/// debe llegar sin tocarlo. FakeTimeProvider avanza el tiempo de apertura a mano.
/// </summary>
public sealed class CircuitBreakerTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp = DateTimeOffset.UtcNow.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan time) => _timestamp += time.Ticks;
    }

    /// <summary>Handler interno que cuenta llamadas y responde/lanza lo que el test decide.</summary>
    private sealed class StubHandler(Func<int, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var call = ++Calls;
            return responder(call);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode code) => new(code);

    private static (StubHandler Stub, FakeTimeProvider Clock, HttpClient Http) New(
        Func<int, Task<HttpResponseMessage>>? responder = null)
    {
        var stub = new StubHandler(responder ?? (_ => Task.FromResult(Response(HttpStatusCode.OK))));
        var clock = new FakeTimeProvider();
        // El breaker como DelegatingHandler de un HttpClient real: misma pila que produce
        // PinnedCaHttpClientFactory (el SDK de AWS consume ese HttpClient).
        var breaker = new CircuitBreakerHandler("test-downstream", clock: clock) { InnerHandler = stub };
        return (stub, clock, new HttpClient(breaker));
    }

    private static Task<HttpResponseMessage> Send(HttpClient http) => http.GetAsync("https://downstream/x");

    [Fact]
    public async Task Three_consecutive_failures_open_and_next_call_fails_fast_without_touching_downstream()
    {
        var (stub, _, http) = New(_ => Task.FromResult(Response(HttpStatusCode.InternalServerError)));

        await Send(http);
        await Send(http);
        await Send(http); // 3 fallos consecutivos → OPEN

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() => Send(http));
        sw.Stop();

        Assert.Equal(3, stub.Calls); // fail-fast: la 4ª NUNCA tocó el downstream
        Assert.True(sw.ElapsedMilliseconds < 1000, $"fail-fast tardó {sw.ElapsedMilliseconds} ms (AC4)");
    }

    [Fact]
    public async Task Failures_are_consecutive_only_a_success_in_between_resets_the_counter()
    {
        var call = 0;
        var (stub, _, http) = New(_ =>
        {
            call++;
            return Task.FromResult(Response(call is 1 or 2 or 4 or 5 ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK));
        });

        await Send(http); // fallo 1
        await Send(http); // fallo 2
        await Send(http); // éxito → contador a cero
        await Send(http); // fallo 1
        await Send(http); // fallo 2 — NO abre (necesita 3 CONSECUTIVOS)

        var response = await Send(http); // pasa: sigue CLOSED
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, stub.Calls);
    }

    [Fact]
    public async Task Http5xx_counts_as_failure_but_4xx_does_not()
    {
        var call = 0;
        var (_, _, http) = New(_ =>
        {
            call++;
            return Task.FromResult(Response(call <= 2 ? HttpStatusCode.BadGateway : HttpStatusCode.NotFound));
        });

        await Send(http); // 502 — fallo 1
        await Send(http); // 502 — fallo 2
        await Send(http); // 404 — éxito (respuesta válida, no indisponibilidad): NO abre
        await Send(http);
        await Send(http);

        // Sigue CLOSED tras 3 "404": nunca abrió.
        var response = await Send(http);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Network_exception_counts_as_failure()
    {
        var (stub, _, http) = New(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("connection refused")));

        await Assert.ThrowsAsync<HttpRequestException>(() => Send(http));
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(http));
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(http)); // 3º → OPEN

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() => Send(http));
        Assert.Equal(3, stub.Calls);
    }

    [Fact]
    public async Task After_open_duration_one_probe_closes_on_success()
    {
        var fail = true;
        var (stub, clock, http) = New(_ =>
            fail ? Task.FromResult(Response(HttpStatusCode.InternalServerError))
                 : Task.FromResult(Response(HttpStatusCode.OK)));

        await Send(http);
        await Send(http);
        await Send(http); // OPEN
        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() => Send(http)); // aún en ventana

        clock.Advance(CircuitBreakerHandler.OpenDuration + TimeSpan.FromMilliseconds(100));
        fail = false;
        var response = await Send(http); // probe exitoso → CLOSED

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, stub.Calls); // 3 fallos + 1 probe
        var next = await Send(http); // cerrado: pasa directo
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(5, stub.Calls);
    }

    [Fact]
    public async Task Probe_failure_reopens_for_another_window()
    {
        var (stub, clock, http) = New(_ => Task.FromResult(Response(HttpStatusCode.InternalServerError)));

        await Send(http);
        await Send(http);
        await Send(http); // OPEN
        clock.Advance(CircuitBreakerHandler.OpenDuration + TimeSpan.FromMilliseconds(100));

        await Send(http); // probe falla → re-OPEN

        await Assert.ThrowsAsync<CircuitBreakerOpenException>(() => Send(http));
        Assert.Equal(4, stub.Calls); // el re-OPEN no tocó red: fail-fast inmediato
    }
}
