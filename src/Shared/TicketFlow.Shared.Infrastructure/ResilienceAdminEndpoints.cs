using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Shared.Infrastructure;

/// <summary>
/// Endpoints admin de resiliencia (D-0012), idénticos en los 4 servicios: inyección de fallos
/// en vivo (POST/GET/DELETE /admin/failure — el evaluador opera sin editar código ni reiniciar)
/// e inspección de la DLQ del worker (GET /admin/dlq — peek sin borrar). Requiere en DI:
/// FailureInjector, IAmazonSQS y SqsSagaTransport. Sin auth: producto local de demo (N-A en QA F4).
/// </summary>
public static class ResilienceAdminEndpoints
{
    public sealed record InjectFailureRequest(string? Step, string? Mode, int? Count);
    public sealed record FailureRuleResponse(string Step, string Mode, int? Remaining);
    public sealed record DlqResponse(IReadOnlyList<SagaDlqEntry> Messages);

    public static void MapResilienceAdmin(this WebApplication app, string consumedQueueName)
    {
        app.MapPost("/admin/failure", async (InjectFailureRequest? request, FailureInjector injector) =>
        {
            if (request is null)
                return Problem(400, "invalid-failure-rule", "El body es requerido: {step, mode, count?}.");
            var step = request.Step?.Trim() ?? "";
            if (step.Length is < 1 or > 64 || !step.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '*' or '_'))
                return Problem(400, "invalid-failure-rule",
                    "El paso debe tener 1..64 caracteres [a-z0-9-_] o '*' (p. ej. 'reserve-seats').");
            var mode = request.Mode?.Trim() ?? "";
            if (mode is not (FailureInjector.Transient or FailureInjector.Persistent))
                return Problem(400, "invalid-failure-rule",
                    $"El modo debe ser '{FailureInjector.Transient}' o '{FailureInjector.Persistent}'.");
            var count = request.Count ?? 2;
            if (count is < 1 or > 10)
                return Problem(400, "invalid-failure-rule", "count debe estar en 1..10 (default 2; ignorado en persistent).");

            injector.Inject(step, mode, count);
            app.Logger.LogInformation(
                "fallo inyectado por admin: paso '{Step}' modo {Mode} (los siguientes intentos fallarán)", step, mode);
            return Results.Created($"/admin/failure/{step}", new FailureRuleResponse(step, mode,
                mode == FailureInjector.Persistent ? null : count));
        });

        app.MapGet("/admin/failure", (FailureInjector injector) =>
            Results.Ok(injector.View().Select(r => new FailureRuleResponse(r.Step, r.Mode, r.Remaining))));

        app.MapDelete("/admin/failure/{step}", (string step, FailureInjector injector) =>
            injector.Remove(step)
                ? Results.NoContent()
                : Problem(404, "failure-rule-not-found", $"No hay regla de fallo para el paso '{step}'."));

        // DLQ del worker de ESTE servicio (peek sin borrar, D-0012).
        app.MapGet("/admin/dlq", async (SqsSagaTransport transport, CancellationToken ct) =>
            Results.Ok(new DlqResponse(await transport.PeekDlqAsync(consumedQueueName, ct))));
    }

    private static IResult Problem(int status, string type, string detail) =>
        Results.Problem(statusCode: status, title: "Invalid failure rule", type: type, detail: detail);
}
