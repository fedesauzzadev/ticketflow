using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Payments.Core.Application;

/// <summary>
/// Handler de authorize-payment (D-0004): decisión determinística (PaymentPolicy), persistencia
/// idempotente por AUTH#purchase_id y evento resultante para Órdenes. Un duplicado re-publica la
/// MISMA decisión sin volver a evaluar (hook H-03 AC2).
/// </summary>
public sealed class PaymentsSagaHandler(IPaymentRepository payments, ILogger<PaymentsSagaHandler> logger)
{
    public async Task<SagaEnvelope?> HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Type != SagaTypes.AuthorizePayment)
        {
            logger.LogWarning("comando de saga desconocido '{Type}' en Pagos: ignorado", envelope.Type);
            return null;
        }

        var command = envelope.PayloadAs<AuthorizePaymentCommand>();
        if (string.IsNullOrWhiteSpace(command.Pan) || command.Amount < 0)
        {
            logger.LogError("authorize-payment de compra {PurchaseId} malformado: descartado", envelope.PurchaseId);
            return null;
        }

        var (approved, reason) = PaymentPolicy.Decide(command.Pan);
        var decidedNow = await payments.SaveIfNewAsync(
            new Payment(envelope.PurchaseId, approved, reason, command.Amount,
                command.Pan[^4..], DateTimeOffset.UtcNow), ct);
        if (!decidedNow)
        {
            var existing = await payments.GetAsync(envelope.PurchaseId, ct)
                           ?? throw new InvalidOperationException(
                               $"pago persistido pero no legible para compra {envelope.PurchaseId}");
            (approved, reason) = (existing.Approved, existing.Reason);
            logger.LogInformation("authorize-payment duplicado de compra {PurchaseId}: decisión previa re-publicada",
                envelope.PurchaseId);
        }

        return approved
            ? Envelope(envelope.PurchaseId, SagaTypes.PaymentApproved, new PaymentApprovedEvent(command.Amount))
            : Envelope(envelope.PurchaseId, SagaTypes.PaymentDeclined,
                new PaymentDeclinedEvent(reason ?? "pago rechazado"));
    }

    private static SagaEnvelope Envelope<T>(string purchaseId, string type, T payload) =>
        new(type, purchaseId, JsonSerializer.SerializeToElement(payload, SagaJson.Options));
}
