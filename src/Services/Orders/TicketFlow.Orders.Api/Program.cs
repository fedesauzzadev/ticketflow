using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using TicketFlow.Orders.Api;
using TicketFlow.Orders.Core.Application;
using TicketFlow.Orders.Core.Domain;
using TicketFlow.Orders.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Logging;
using TicketFlow.Shared.Infrastructure.Saga;

var builder = WebApplication.CreateBuilder(args);

// Logging JSON de consola con purchase_id en scope: la saga se narra sola (D-0003).
// Contrato de traza D-0011: Timestamp ISO UTC ordenable (trace-purchase.ps1).
builder.Logging.AddServiceTraceLogging();

// Config 100% por env, validada fail-fast al arranque (NFR-1 / D-0003).
var awsOptions = EmulatorAwsOptions.FromEnvironment();
builder.Services.AddSingleton(awsOptions);
builder.Services.AddSingleton<IAmazonDynamoDB>(_ => EmulatorAwsClients.CreateDynamoDb(awsOptions));
builder.Services.AddSingleton<IAmazonSQS>(_ => EmulatorAwsClients.CreateSqs(awsOptions));
builder.Services.AddSingleton<SqsSagaTransport>();

builder.Services.AddSingleton<DynamoDbPurchaseRepository>();
builder.Services.AddSingleton<IPurchaseRepository>(sp => sp.GetRequiredService<DynamoDbPurchaseRepository>());
builder.Services.AddSingleton<DynamoDbOutbox>();
builder.Services.AddSingleton<IOutbox>(sp => sp.GetRequiredService<DynamoDbOutbox>());
builder.Services.AddSingleton<DynamoDbIdempotencyStore>();
builder.Services.AddSingleton<IIdempotencyStore>(sp => sp.GetRequiredService<DynamoDbIdempotencyStore>());
builder.Services.AddSingleton<ICatalogAvailabilityGateway>(
    _ => new HttpCatalogGateway(Environment.GetEnvironmentVariable("CATALOG_BASE_URL"))); // D-0009
builder.Services.AddSingleton<SagaProcessManager>();
builder.Services.AddHostedService<OutboxRelayWorker>();
builder.Services.AddHostedService<OrdersEventsWorker>();

// Contrato HTTP en snake_case ({purchase_id, ...}); reason/ticket_ids se omiten si son null (plan §2).
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var port = Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "5081"; // D-0008: Órdenes = 5081
builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

await app.Services.GetRequiredService<DynamoDbPurchaseRepository>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<DynamoDbOutbox>().InitializeAsync(CancellationToken.None);
await app.Services.GetRequiredService<DynamoDbIdempotencyStore>().InitializeAsync(CancellationToken.None);
app.Logger.LogInformation("órdenes listo en :{Port} — tablas {Purchases} + {Outbox} + {Idempotency} aseguradas",
    port, DynamoDbPurchaseRepository.TableName, DynamoDbOutbox.TableName, DynamoDbIdempotencyStore.TableName);

// Errores 500 sin internals (NFR-4).
app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
    Results.Problem(statusCode: 500, title: "Internal error", type: "internal-error").ExecuteAsync(ctx)));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Saga (H-02 AC1): acepta la compra y responde 201 inmediato con purchase_id (D-0004, polling).
// Idempotencia observable (H-03, D-0009): misma key + mismo body ⇒ misma compra sin efectos
// nuevos (200); misma key + body distinto ⇒ 422; key nueva + agotado ⇒ 422 sin consumir la key.
app.MapPost("/purchases", async (HttpRequest http, IPurchaseRepository purchases, IOutbox outbox,
    IIdempotencyStore idempotency, ICatalogAvailabilityGateway catalog, ILogger<Program> logger,
    CancellationToken ct) =>
{
    var keyError = PurchaseRequestValidator.ValidateIdempotencyKey(
        http.Headers.TryGetValue("Idempotency-Key", out var keyHeader) ? keyHeader.ToString() : null);
    if (keyError is not null)
        return Results.Problem(statusCode: 400, title: "Invalid Idempotency-Key",
            type: "invalid-idempotency-key", detail: keyError);
    var key = keyHeader.ToString();

    PurchaseRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<PurchaseRequest>(http.Body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }
    catch (JsonException)
    {
        return Results.Problem(statusCode: 400, title: "Invalid body", type: "invalid-body",
            detail: "El body debe ser JSON válido: {event_id, seats, card{pan, holder, expiry, cvv}}.");
    }
    if (request is null)
        return Results.Problem(statusCode: 400, title: "Invalid body", type: "invalid-body",
            detail: "El body es requerido: {event_id, seats, card{pan, holder, expiry, cvv}}.");

    var errors = PurchaseRequestValidator.Validate(request);
    if (errors.Count > 0)
        return Results.Problem(statusCode: 400, title: "Invalid purchase request",
            type: "invalid-purchase-request", detail: string.Join("; ", errors));

    // H-03 AC1: peek de la key — el replay gana sobre todo lo demás (ni disponibilidad se revalida).
    var requestHash = PurchaseBodyHash.Of(request);
    var existing = await idempotency.FindAsync(key, ct);
    if (existing is not null)
        return await ResolveExistingKeyAsync(existing, request, requestHash, key, purchases, outbox, logger, ct);

    // H-03 AC3: validación sincrónica de disponibilidad (plan §2: 422 si agotados). Fail-open
    // si el Catálogo no responde — el guard real sigue siendo el decremento condicional (D-0009).
    // Antes del claim: un 422 no debe consumir la key (reintentar cuando haya asientos).
    var available = await catalog.GetAvailabilityAsync(request.EventId!, ct);
    if (available is int seatsLeft && seatsLeft < request.Seats!.Value)
    {
        logger.LogInformation("compra rechazada por disponibilidad: evento {EventId} tiene {Available}, se pidieron {Seats}",
            request.EventId, seatsLeft, request.Seats);
        return Results.Problem(statusCode: 422, title: "Seats unavailable", type: "seats-unavailable",
            detail: $"El evento '{request.EventId}' no tiene disponibilidad suficiente: se pidieron " +
                    $"{request.Seats} asiento(s) y quedan {seatsLeft}.");
    }

    var purchase = Purchase.Create(
        PurchaseIds.New(), request.EventId!, request.Seats!.Value, request.Card!.Pan!, key, DateTimeOffset.UtcNow);
    if (!await idempotency.TryClaimAsync(key, requestHash, purchase.PurchaseId, ct))
    {
        // Carrera: otra request con la misma key ganó el claim mientras hacíamos el peek.
        var winner = await idempotency.FindAsync(key, ct);
        if (winner is null)
            return Results.Problem(statusCode: 500, title: "Internal error", type: "internal-error");
        return await ResolveExistingKeyAsync(winner, request, requestHash, key, purchases, outbox, logger, ct);
    }

    await purchases.CreateAsync(purchase, ct);
    // Comando inicial via outbox: si el proceso muere antes del relay, el comando sobrevive.
    await outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats, purchase.PurchaseId,
        new ReserveSeatsCommand(purchase.EventId, purchase.Seats), ct);

    logger.LogInformation("compra {PurchaseId} creada PENDING para {EventId} x{Seats} (key {Key})",
        purchase.PurchaseId, purchase.EventId, purchase.Seats, purchase.IdempotencyKey);
    return Results.Created($"/purchases/{purchase.PurchaseId}",
        new PurchaseResponse(purchase.PurchaseId, purchase.Status.ToString(), null, null, purchase.CreatedAt));
});

// Key ya registrada (D-0009): hash igual ⇒ la MISMA compra (replay 200, heal de ventana de crash);
// hash distinto ⇒ 422 explícito — misma key nunca puede significar dos bodies distintos.
static async Task<IResult> ResolveExistingKeyAsync(
    IdempotencyClaim claim, PurchaseRequest request, string requestHash, string key,
    IPurchaseRepository purchases, IOutbox outbox, ILogger logger, CancellationToken ct)
{
    if (claim.RequestHash != requestHash)
    {
        logger.LogInformation("key {Key} reusada con body distinto (compra {PurchaseId}): rechazada 422",
            key, claim.PurchaseId);
        return Results.Problem(statusCode: 422, title: "Idempotency-Key reused with different body",
            type: "idempotency-key-reused",
            detail: "El Idempotency-Key ya fue usado con un body distinto. Reutilizalo con el mismo body " +
                    "exacto o enviá uno nuevo.");
    }

    var purchase = await purchases.GetAsync(claim.PurchaseId, ct);
    if (purchase is null)
    {
        // Ventana de crash entre claim y create: la key es dueña del id → el replay la completa.
        purchase = Purchase.Create(claim.PurchaseId, request.EventId!, request.Seats!.Value,
            request.Card!.Pan!, key, DateTimeOffset.UtcNow);
        await purchases.CreateAsync(purchase, ct);
        await outbox.EnqueueAsync(SagaQueues.CatalogCommands, SagaTypes.ReserveSeats, purchase.PurchaseId,
            new ReserveSeatsCommand(purchase.EventId, purchase.Seats), ct);
        logger.LogInformation("compra {PurchaseId} completada por replay (claim previo sin compra)", purchase.PurchaseId);
        return Results.Created($"/purchases/{purchase.PurchaseId}",
            new PurchaseResponse(purchase.PurchaseId, purchase.Status.ToString(), null, null, purchase.CreatedAt));
    }

    logger.LogInformation("replay con key {Key}: misma compra {PurchaseId} ({Status}) — sin efectos nuevos",
        key, purchase.PurchaseId, purchase.Status);
    return Results.Ok(new PurchaseResponse(purchase.PurchaseId, purchase.Status.ToString(),
        purchase.FailureReason, purchase.TicketIds, purchase.CreatedAt));
}

// Estado compartido observable (H-02 AC4).
app.MapGet("/purchases/{id}", async (string id, IPurchaseRepository purchases, CancellationToken ct) =>
{
    if (!PurchaseIds.IsValid(id))
        return Results.Problem(statusCode: 400, title: "Invalid purchase id", type: "invalid-purchase-id",
            detail: "El id de compra debe tener formato 'pur-' + 32 hex.");

    var purchase = await purchases.GetAsync(id, ct);
    return purchase is null
        ? Results.Problem(statusCode: 404, title: "Purchase not found", type: "purchase-not-found",
            detail: $"La compra '{id}' no existe.")
        : Results.Ok(new PurchaseResponse(
            purchase.PurchaseId, purchase.Status.ToString(), purchase.FailureReason, purchase.TicketIds,
            purchase.CreatedAt));
});

app.Run();

public partial class Program { }

public sealed record PurchaseResponse(
    string PurchaseId, string Status, string? Reason, IReadOnlyList<string>? TicketIds, DateTimeOffset CreatedAt);
