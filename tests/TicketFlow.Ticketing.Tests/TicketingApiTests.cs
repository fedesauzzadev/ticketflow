using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Ticketing.Core.Application;
using TicketFlow.Ticketing.Core.Infrastructure;

namespace TicketFlow.Ticketing.Tests;

/// <summary>
/// Contrato HTTP del servicio Ticketing contra el emulador real (QA F-07 / H-10 AC3):
/// GET /tickets/{id} responde la entrada referenciable y 404/400 ProblemDetails.
/// Arranca el Program completo in-proc (patrÃ³n Catalog/Orders), seed por repo.
/// </summary>
public sealed class TicketingApiTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static DynamoDbTicketRepository NewRepo() =>
        new(EmulatorAwsClients.CreateDynamoDb(EmulatorAwsOptions.FromEnvironment()));

    [SkippableFact]
    public async Task Get_ticket_returns_200_with_referenced_fields()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var repo = NewRepo();
        await repo.InitializeAsync(ct);
        var ticketId = "tkt-" + Guid.NewGuid().ToString("N");
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");
        var qrKey = $"qr/{ticketId}";
        await repo.SaveAsync(new Ticket(ticketId, purchaseId, "evt-mj-tribute-night", Seat: 1, QrS3Key: qrKey), ct);

        using var factory = NewFactory();
        var client = factory.CreateClient();

        var ticket = await client.GetFromJsonAsync<JsonElement>($"/tickets/{ticketId}");

        Assert.Equal(ticketId, ticket.GetProperty("id").GetString());
        Assert.Equal(purchaseId, ticket.GetProperty("purchase_id").GetString());
        Assert.Equal("evt-mj-tribute-night", ticket.GetProperty("event_id").GetString());
        Assert.Equal(1, ticket.GetProperty("seat").GetInt32());
        Assert.Equal(qrKey, ticket.GetProperty("qr_s3_key").GetString()); // QR referenciable (H-02 AC1)
    }

    [SkippableFact]
    public async Task Get_unknown_ticket_returns_404_and_short_id_returns_400()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var notFound = await client.GetAsync("/tickets/tkt-0000000000000000000000000000dead");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var badId = await client.GetAsync("/tickets/xx");
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
    }
}
