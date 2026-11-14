namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// Fallo inyectable en memoria (D-0012): el evaluador activa fallos transitorios o
/// persistentes por paso de saga vía POST /admin/failure — sin editar código ni reiniciar.
/// Lo consulta <c>SqsSagaWorker</c> antes de cada intento de handler (paso exacto o "*").
/// Regla transient: falla <c>count</c> intentos y deja de fallar (AC1 exacto con count=2
/// + 2 reintentos). Regla persistent: falla siempre hasta DELETE.
/// </summary>
public sealed class FailureInjector
{
    public const string Transient = "transient";
    public const string Persistent = "persistent";

    private sealed record Rule(string Step, string Mode, int Remaining);

    /// <summary>Fallo que este injector decide aplicar a un intento (mensaje para el log).</summary>
    public sealed record InjectedFailure(string Step, string Mode);

    private readonly object _gate = new();
    private readonly Dictionary<string, Rule> _rules = new(StringComparer.Ordinal);

    public void Inject(string step, string mode, int count)
    {
        lock (_gate) _rules[step] = new Rule(step, mode, mode == Persistent ? int.MaxValue : count);
    }

    /// <summary>Consume un intento contra la regla del paso (exacta o comodín "*"): null = no fallar.</summary>
    public InjectedFailure? TryConsume(string step)
    {
        lock (_gate)
        {
            if (!_rules.TryGetValue(step, out var rule) && !_rules.TryGetValue("*", out rule))
                return null;
            if (rule.Mode == Persistent)
                return new InjectedFailure(rule.Step, rule.Mode);
            if (rule.Remaining <= 0)
            {
                _rules.Remove(rule.Step);
                return null;
            }
            var remaining = rule.Remaining - 1;
            if (remaining == 0) _rules.Remove(rule.Step); // agotada: las siguientes pasan
            else _rules[rule.Step] = rule with { Remaining = remaining };
            return new InjectedFailure(rule.Step, rule.Mode);
        }
    }

    /// <summary>Vista para GET /admin/failure (remaining null = persistent, no se agota).</summary>
    public IReadOnlyList<(string Step, string Mode, int? Remaining)> View()
    {
        lock (_gate)
        {
            return [.. _rules.Values
                .OrderBy(r => r.Step)
                .Select(r => (r.Step, r.Mode, r.Mode == Persistent ? null : (int?)r.Remaining))];
        }
    }

    public bool Remove(string step)
    {
        lock (_gate) return _rules.Remove(step);
    }
}

/// <summary>Fallo de handler decidido por <see cref="FailureInjector"/>: participa del backoff/DLQ como cualquier fallo.</summary>
public sealed class InjectedFailureException(string message) : Exception(message);
