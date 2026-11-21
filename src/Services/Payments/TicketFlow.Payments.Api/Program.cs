using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using TicketFlow.Payments.Core.Application;
using TicketFlow.Payments.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Logging;
using TicketFlow.Shared.Infrastructure.Saga;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddServiceTraceLogging(); // JSON + Timestamp ISO UTC (contrato de traza D-0011)

var awsOptions = EmulatorAwsOptions.FromEnvironment();
builder.Services.AddSingleton(awsOptions);
// ILoggerFactory → circuit breakers por downstream loguean apertura/cierre (D-0012).
builder.Services.AddSingleton<IAmazonDynamoDB>(
    sp => EmulatorAwsClients.CreateDynamoDb(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IAmazonSQS>(
    sp => EmulatorAwsClients.CreateSqs(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<SqsSagaTransport>();
builder.Services.AddSingleton<TicketFlow.Shared.Infrastructure.FailureInjector>(); // D-0012: /admin/failure
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
// SERVICE_HOST=0.0.0.0 en contenedores (D-0023); localhost por defecto: no expone el servicio a la red.
var host = Environment.GetEnvironmentVariable("SERVICE_HOST") ?? "localhost";
builder.WebHost.UseUrls($"http://{host}:{port}");

var app = builder.Build();

await app.Services.GetRequiredService<DynamoDbPaymentRepository>().InitializeAsync(CancellationToken.None);
app.Logger.LogInformation("pagos listo en :{Port} — tabla {Table} asegurada", port, DynamoDbPaymentRepository.TableName);

app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
    Results.Problem(statusCode: 500, title: "Internal error", type: "internal-error").ExecuteAsync(ctx)));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Resiliencia observable (D-0012): inyección de fallos en vivo + DLQ del worker de este servicio.
app.MapResilienceAdmin(SagaQueues.PaymentsCommands);

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
    TicketFlow.Shared.Infrastructure.FailureInjector failureInjector,
    ILogger<PaymentsWorker> logger)
    : SqsSagaWorker(sqs, transport, SagaQueues.PaymentsCommands, logger, failureInjector)
{
    protected override async Task HandleAsync(SagaEnvelope envelope, CancellationToken ct)
    {
        if (await handler.HandleAsync(envelope, ct) is { } @event)
            await Transport.SendAsync(SagaQueues.OrdersEvents, @event, ct);
    }
}
