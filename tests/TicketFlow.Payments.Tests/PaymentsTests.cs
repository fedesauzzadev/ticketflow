using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Payments.Core.Application;
using TicketFlow.Payments.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

namespace TicketFlow.Payments.Tests;

/// <summary>
/// Reglas del simulador (unit, sin red) + handler de authorize-payment contra DynamoDB real:
/// decisión idempotente por AUTH#purchase_id — el duplicado re-publica la misma decisión.
/// </summary>
public sealed class PaymentsTests
{
    [Theory]
    [InlineData("4111111111110002", false)] // termina 0002 ⇒ rechazada (H-02 AC3)
    [InlineData("5105105105100002", false)]
    [InlineData("4111111111111111", true)]  // cualquier otra ⇒ aprobada (H-02 AC1)
    [InlineData("370000000000001", true)]   // 15 dígitos, no termina 0002
    public void Policy_decides_by_pan_suffix(string pan, bool expectedApproved)
    {
        var (approved, reason) = PaymentPolicy.Decide(pan);
        Assert.Equal(expectedApproved, approved);
        if (!expectedApproved)
        {
            Assert.NotNull(reason);
            Assert.Contains(PaymentPolicy.DecliningSuffix, reason); // motivo visible
        }
        else
        {
            Assert.Null(reason);
        }
    }

    private static DynamoDbPaymentRepository NewRepo() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    private static SagaEnvelope Command(string purchaseId, string pan, decimal amount) =>
        new(SagaTypes.AuthorizePayment, purchaseId,
            JsonSerializer.SerializeToElement(new AuthorizePaymentCommand(pan, amount), SagaJson.Options));

    [SkippableFact]
    public async Task Authorize_persists_once_and_republishes_same_decision_on_duplicate()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var handler = new PaymentsSagaHandler(repo, NullLogger<PaymentsSagaHandler>.Instance);
        await repo.InitializeAsync(ct);
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");

        var first = await handler.HandleAsync(Command(purchaseId, "4111111111111111", 241.00m), ct);
        Assert.NotNull(first);
        Assert.Equal(SagaTypes.PaymentApproved, first.Type);
        var amount = first.PayloadAs<PaymentApprovedEvent>().Amount;
        Assert.Equal(241.00m, amount);

        var duplicate = await handler.HandleAsync(Command(purchaseId, "5105105105100002", 241.00m), ct);
        Assert.NotNull(duplicate);
        Assert.Equal(SagaTypes.PaymentApproved, duplicate.Type); // la decisión previa persiste: PAN no re-evalúa
        Assert.Equal(amount, duplicate.PayloadAs<PaymentApprovedEvent>().Amount);

        var stored = await repo.GetAsync(purchaseId, ct);
        Assert.NotNull(stored);
        Assert.True(stored.Approved);
        Assert.Equal("1111", stored.PanLast4);
    }

    [SkippableFact]
    public async Task Declined_pan_publishes_declined_event_with_reason()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        var handler = new PaymentsSagaHandler(repo, NullLogger<PaymentsSagaHandler>.Instance);
        await repo.InitializeAsync(ct);
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");

        var declined = await handler.HandleAsync(Command(purchaseId, "5105105105100002", 99m), ct);

        Assert.NotNull(declined);
        Assert.Equal(SagaTypes.PaymentDeclined, declined.Type);
        Assert.Contains(PaymentPolicy.DecliningSuffix, declined.PayloadAs<PaymentDeclinedEvent>().Reason);
        var stored = await repo.GetAsync(purchaseId, ct);
        Assert.False(stored!.Approved);
    }
}
