using Microsoft.Extensions.Logging;

namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// Circuit breaker OPEN: fail-fast explícito mientras el downstream está dado de baja
/// (D-0012). Vuelve mucho antes del timeout de 10 s — es la señal observable de AC4.
/// </summary>
public sealed class CircuitBreakerOpenException(string downstream) : InvalidOperationException(
    $"circuit breaker OPEN para '{downstream}': fail-fast (downstream tras fallos consecutivos)");

/// <summary>
/// Circuit breaker propio (D-0012, ~80 líneas en vez de Polly): DelegatingHandler dentro de
/// <c>PinnedCaHttpClientFactory</c> → un breaker POR downstream (instancia de HttpClient del
/// SDK: DynamoDB, SQS, S3). Máquina de estados determinista, reloj inyectable para tests:
/// CLOSED → 3 fallos consecutivos (excepción/timeout/5xx) → OPEN 5 s (fail-fast + log) →
/// HALF_OPEN con UN probe → éxito: CLOSED (log) · fallo: OPEN de nuevo.
/// shortcut: sin métricas por llamada ni jitter — con logs de apertura/cierre alcanza para H-06.
/// </summary>
public sealed class CircuitBreakerHandler(string downstream, ILogger? logger = null, TimeProvider? clock = null)
    : DelegatingHandler
{
    public const int FailureThreshold = 3;
    public static readonly TimeSpan OpenDuration = TimeSpan.FromSeconds(5);

    private enum State { Closed, Open, HalfOpen }

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private State _state = State.Closed;
    private int _consecutiveFailures;
    private long _openedAt;
    private int _probeInFlight;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var probe = false;
        lock (_gate)
        {
            if (_state == State.Open)
            {
                if (_clock.GetElapsedTime(_openedAt) < OpenDuration)
                    throw new CircuitBreakerOpenException(downstream); // fail-fast: sin tocar la red (AC4)
                _state = State.HalfOpen; // ventana cumplida: esta llamada es el probe
            }
            if (_state == State.HalfOpen)
            {
                if (_probeInFlight > 0)
                    throw new CircuitBreakerOpenException(downstream); // solo un probe a la vez
                _probeInFlight = 1;
                probe = true;
            }
        }

        try
        {
            var response = await base.SendAsync(request, ct);
            Record((int)response.StatusCode < 500, probe); // 5xx = downstream enfermo; 4xx es respuesta válida
            return response;
        }
        catch (Exception ex) when (ex is not CircuitBreakerOpenException)
        {
            Record(success: false, probe); // timeout del HttpClient (TaskCanceled) o excepción de red
            throw;
        }
    }

    private void Record(bool success, bool probe)
    {
        lock (_gate)
        {
            if (probe) _probeInFlight = 0;
            if (success)
            {
                if (_state != State.Closed)
                    logger?.LogInformation(
                        "circuit breaker de {Downstream} CLOSED: prueba exitosa, downstream recuperado",
                        downstream);
                _state = State.Closed;
                _consecutiveFailures = 0;
                return;
            }
            _consecutiveFailures++;
            if (probe || _consecutiveFailures >= FailureThreshold)
            {
                _state = State.Open;
                _openedAt = _clock.GetTimestamp();
                logger?.LogWarning(
                    "circuit breaker de {Downstream} OPEN tras {Failures} fallo(s) consecutivo(s): " +
                    "fail-fast durante {Seconds} s y luego una prueba de recuperación",
                    downstream, probe ? FailureThreshold : _consecutiveFailures, OpenDuration.TotalSeconds);
                _consecutiveFailures = 0;
            }
        }
    }
}
