using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using TicketFlow.Payments.Core.Application;
using TicketFlow.Payments.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Saga;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole();

var awsOptions = EmulatorAwsOptions.FromEnvironment();
builder.Services.AddSingleton(awsOptions);
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => EmulatorAwsClients.CreateDynamoDb(awsOptions));
builder.Services.AddSingleton<IAmazonSQS>(_ => EmulatorAwsClients.CreateSqs(awsOptions));
builder.Services.AddSingleton<SqsSagaTransport>();
builder.Services.AddSingleton<DynamoDbPaymentRepository>();
builder.Services.AddSingleton<IPaymentRepository>(sp => sp.GetRequiredService<DynamoDbPaymentRepository>());
builder.Services.AddSingleton<PaymentsSagaHandler>();
builder.Services.AddHostedService<PaymentsWorker>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var port = Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "5082"; // D-0008: Pagos = 5082
builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

await app.Services.GetRequiredService<DynamoDbPaymentRepository>().InitializeAsync(CancellationToken.None);
app.Logger.LogInformation("pagos listo en :{Port} — tabla {Table} asegurada", port, DynamoDbPaymentRepository.TableName);

app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
    Results.Problem(statusCode: 500, title: "Internal error", type: "internal-error").ExecuteAsync(ctx)));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Evidencia del paso de pago (plan §2): {purchase_id, status: APPROVED|DECLINED, reason?}.
app.MapGet("/payments/{purchaseId}", async (string purchaseId, IPaymentRepository payments, CancellationToken ct) =>
{
    if (purchaseId.Length is < 4 or > 64)
        return Results.Problem(statusCode: 400, title: "Invalid purchase id", type: "invalid-purchase-id",
            detail: "El id de compra no tiene un formato válido.");

    var payment = await payments.GetAsync(purchaseId, ct);
    return payment is null
        ? Results.Problem(statusCode: 404, title: "Payment not found", type: "payment-not-found",
            detail: $"No hay autorización registrada para la compra '{purchaseId}'.")
        : Results.Ok(new PaymentResponse(payment.PurchaseId, payment.Approved ? "APPROVED" : "DECLINED",
            payment.Reason, payment.Amount));
});

app.Run();

public partial class Program { }

public sealed record PaymentResponse(string PurchaseId, string Status, string? Reason, decimal Amount);

/// <summary>Worker delgado de Pagos: delega en PaymentsSagaHandler y publica el evento resultante.</summary>
public sealed class PaymentsWorker(
    IAmazonSQS sqs,
    SqsSagaTransport transport,
    PaymentsSagaHandler handler,
    ILogger<PaymentsWorker> logger)
    : SqsSagaWorker(sqs, transport, SagaQueues.PaymentsCommands, logger)
{
    protected override async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        if (await handler.HandleAsync(envelope, ct) is { } @event)
            await Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
    }
}
