using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TicketFlow.Payments.Core.Application;
using TicketFlow.Payments.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;

namespace TicketFlow.Payments.Tests;

/// <summary>
/// Contrato HTTP del servicio Pagos contra el emulador real (QA F-07 / H-10 AC3):
/// GET /payments/{purchase_id} responde la decisiÃ³n persistida y 404/400 ProblemDetails.
/// Arranca el Program completo in-proc (patrÃ³n Catalog/Orders), seed por repo.
/// </summary>
public sealed class PaymentsApiTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static DynamoDbPaymentRepository NewRepo() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    [SkippableFact]
    public async Task Get_payment_returns_200_with_decision_and_amount()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        await repo.InitializeAsync(ct);
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        Assert.True(await repo.SaveIfNewAsync(
            new Payment(purchaseId, Approved: true, Reason: null, Amount: 120.50m, PanLast4: "1111",
                DecidedAt: DateTimeOffset.UtcNow), ct));

        using var factory = NewFactory();
        var client = factory.CreateClient();

        var payment = await client.GetFromJsonAsync<JsonElement>($"/payments/{purchaseId}");

        Assert.Equal(purchaseId, payment.GetProperty("purchase_id").GetString());
        Assert.Equal("APPROVED", payment.GetProperty("status").GetString()); // plan Â§2
        Assert.Equal(120.50m, payment.GetProperty("amount").GetDecimal());
    }

    [SkippableFact]
    public async Task Get_unknown_payment_returns_404_and_short_id_returns_400()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var notFound = await client.GetAsync("/payments/pur-00000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var badId = await client.GetAsync("/payments/xx");
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
    }
}
