using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;
using TicketFlow.Ticketing.Core.Application;
using TicketFlow.Ticketing.Core.Infrastructure;

namespace TicketFlow.Ticketing.Tests;

/// <summary>
/// Emisión de entradas contra DynamoDB + S3 reales: N asientos ⇒ N tickets con payload QR
/// referenciable (H-02 AC1) y claim idempotente por compra — el duplicado NO emite nuevas.
/// </summary>
public sealed class TicketingTests
{
    private static (DynamoDbTicketRepository Tickets, S3QrPayloadStore Qr, TicketingSagaHandler Handler) NewHandler()
    {
        var options = EmulatorAwsOptions.FromEnvironment();
        var tickets = new DynamoDbTicketRepository(EmulatorAwsClients.CreateDynamoDb(options));
        var qr = new S3QrPayloadStore(EmulatorAwsClients.CreateS3(options));
        return (tickets, qr,
            new TicketingSagaHandler(tickets, qr, NullLogger<TicketingSagaHandler>.Instance));
    }

    private static SagaEnvelope Command(string purchaseId, int seats) =>
        new(SagaTypes.IssueTickets, purchaseId,
            JsonSerializer.SerializeToElement(new IssueTicketsCommand("it-evt", seats), SagaJson.Options));

    [SkippableFact]
    public async Task Issue_tickets_persists_each_ticket_with_qr_payload_in_s3()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (tickets, qr, handler) = NewHandler();
        await tickets.InitializeAsync(ct);
        await qr.InitializeAsync(ct);
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");

        var @event = await handler.HandleAsync(Command(purchaseId, 3), ct);

        Assert.NotNull(@event);
        Assert.Equal(SagaTypes.TicketsIssued, @event.Type);
        var ids = @event.PayloadAs<TicketsIssuedEvent>().TicketIds;
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());

        foreach (var (index, id) in ids.Index())
        {
            var ticket = await tickets.GetAsync(id, ct);
            Assert.NotNull(ticket);
            Assert.Equal(purchaseId, ticket.PurchaseId);
            Assert.Equal(index + 1, ticket.Seat);
            var payload = await qr.GetAsync(ticket.QrS3Key, ct);
            Assert.NotNull(payload);
            Assert.Contains(id, payload); // QR referenciable en S3 (H-02 AC1)
        }
    }

    [SkippableFact]
    public async Task Duplicate_issue_returns_same_ids_without_new_tickets()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var ct = CancellationToken.None;
        var (tickets, qr, handler) = NewHandler();
        await tickets.InitializeAsync(ct);
        await qr.InitializeAsync(ct);
        var purchaseId = "pur-" + Guid.NewGuid().ToString("N");

        var first = (await handler.HandleAsync(Command(purchaseId, 2), ct))!;
        var second = (await handler.HandleAsync(Command(purchaseId, 2), ct))!; // redelivery del comando

        var firstIds = first.PayloadAs<TicketsIssuedEvent>().TicketIds;
        var secondIds = second.PayloadAs<TicketsIssuedEvent>().TicketIds;
        Assert.Equal(firstIds, secondIds); // mismos ids (hook H-03 AC2: duplicado descartado)
        Assert.Equal(firstIds, await tickets.GetIssuedTicketIdsAsync(purchaseId, ct));
    }

    [SkippableFact]
    public async Task Get_missing_ticket_returns_null()
    {
        EmulatorTestSupport.SkipIfNotConfigured();
        var (tickets, _, _) = NewHandler();
        await tickets.InitializeAsync(CancellationToken.None);
        Assert.Null(await tickets.GetAsync("tkt-0000000000000000000000000000dead", CancellationToken.None));
    }
}
