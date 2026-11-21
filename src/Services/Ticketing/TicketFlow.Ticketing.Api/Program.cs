using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SQS;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Logging;
using TicketFlow.Shared.Infrastructure.Saga;
using TicketFlow.Ticketing.Core.Application;
using TicketFlow.Ticketing.Core.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddServiceTraceLogging(); // JSON + Timestamp ISO UTC (contrato de traza D-0011)

var awsOptions = EmulatorAwsOptions.FromEnvironment();
builder.Services.AddSingleton(awsOptions);
// ILoggerFactory → circuit breakers por downstream loguean apertura/cierre (D-0012).
builder.Services.AddSingleton<IAmazonDynamoDB>(
    sp => EmulatorAwsClients.CreateDynamoDb(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IAmazonSQS>(
    sp => EmulatorAwsClients.CreateSqs(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IAmazonS3>(
    sp => EmulatorAwsClients.CreateS3(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<SqsSagaTransport>();
builder.Services.AddSingleton<TicketFlow.Shared.Infrastructure.FailureInjector>(); // D-0012: /admin/failure
builder.Services.AddSingleton<DynamoDbTicketRepository>();
builder.Services.AddSingleton<ITicketRepository>(sp => sp.GetRequiredService<DynamoDbTicketRepository>());
builder.Services.AddSingleton<IQrPayloadStore, S3QrPayloadStore>();
builder.Services.AddSingleton<TicketingSagaHandler>();
builder.Services.AddHostedService<TicketingWorker>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var port = Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "5083"; // D-0008: Ticketing = 5083
// SERVICE_HOST=0.0.0.0 en contenedores (D-0023); localhost por defecto: no expone el servicio a la red.
var host = Environment.GetEnvironmentVariable("SERVICE_HOST") ?? "localhost";
builder.WebHost.UseUrls($"http://{host}:{port}");

var app = builder.Build();

await app.Services.GetRequiredService<DynamoDbTicketRepository>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<IQrPayloadStore>().InitializeAsync(CancellationToken.None);
app.Logger.LogInformation("ticketing listo en :{Port} — tabla {Table} y bucket {Bucket} asegurados",
    port, DynamoDbTicketRepository.TableName, S3QrPayloadStore.BucketName);

app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
    Results.Problem(statusCode: 500, title: "Internal error", type: "internal-error").ExecuteAsync(ctx)));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Resiliencia observable (D-0012): inyección de fallos en vivo + DLQ del worker de este servicio.
app.MapResilienceAdmin(SagaQueues.TicketingCommands);

// Entrada referenciable (H-02 AC1): {id, purchase_id, event_id, seat, qr_s3_key}.
app.MapGet("/tickets/{id}", async (string id, ITicketRepository tickets, CancellationToken ct) =>
{
    if (id.Length is < 4 or > 64)
        return Results.Problem(statusCode: 400, title: "Invalid ticket id", type: "invalid-ticket-id",
            detail: "El id de entrada no tiene un formato válido.");

    var ticket = await tickets.GetAsync(id, ct);
    return ticket is null
        ? Results.Problem(statusCode: 404, title: "Ticket not found", type: "ticket-not-found",
            detail: $"La entrada '{id}' no existe.")
        : Results.Ok(new TicketResponse(ticket.Id, ticket.PurchaseId, ticket.EventId, ticket.Seat, ticket.QrS3Key));
});

app.Run();

public partial class Program { }

public sealed record TicketResponse(string Id, string PurchaseId, string EventId, int Seat, string QrS3Key);

/// <summary>Worker delgado de Ticketing: delega en TicketingSagaHandler y publica el evento resultante.</summary>
public sealed class TicketingWorker(
    IAmazonSQS sqs,
    SqsSagaTransport transport,
    TicketingSagaHandler handler,
    TicketFlow.Shared.Infrastructure.FailureInjector failureInjector,
    ILogger<TicketingWorker> logger)
    : SqsSagaWorker(sqs, transport, SagaQueues.TicketingCommands, logger, failureInjector)
{
    protected override async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        if (await handler.HandleAsync(envelope, ct) is { } @event)
            await Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
    }
}
