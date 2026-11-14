using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.SQS;
using TicketFlow.Catalog.Api;
using TicketFlow.Catalog.Core.Application;
using TicketFlow.Catalog.Core.Domain;
using TicketFlow.Catalog.Core.Infrastructure;
using TicketFlow.Shared.Infrastructure;
using TicketFlow.Shared.Infrastructure.Logging;
using TicketFlow.Shared.Infrastructure.Saga;

var builder = WebApplication.CreateBuilder(args);

// Logging JSON de consola, cero deps extra (D-0003). Contrato de traza D-0011.
builder.Logging.AddServiceTraceLogging();

// Config 100% por env, validada fail-fast al arranque (NFR-1 / D-0003).
var awsOptions = EmulatorAwsOptions.FromEnvironment();
builder.Services.AddSingleton(awsOptions);
// ILoggerFactory → los circuit breakers por downstream loguean apertura/cierre (D-0012).
builder.Services.AddSingleton<IAmazonDynamoDB>(
    sp => EmulatorAwsClients.CreateDynamoDb(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<IAmazonSQS>(
    sp => EmulatorAwsClients.CreateSqs(awsOptions, sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<TicketFlow.Shared.Infrastructure.Saga.SqsSagaTransport>();
builder.Services.AddSingleton<TicketFlow.Shared.Infrastructure.FailureInjector>(); // D-0012: /admin/failure

builder.Services.AddHybridCache(); // cache in-app (D-0007)
builder.Services.AddSingleton<AvailabilityCacheOptions>();
builder.Services.AddSingleton<DynamoDbEventRepository>();
builder.Services.AddSingleton<IEventRepository>(sp => sp.GetRequiredService<DynamoDbEventRepository>());
builder.Services.AddSingleton<TicketFlow.Catalog.Core.Application.ISeatReservationStore>(
    sp => sp.GetRequiredService<DynamoDbEventRepository>());
builder.Services.AddSingleton<AvailabilityService>();
builder.Services.AddSingleton<CatalogSagaHandler>();
builder.Services.AddHostedService<CatalogSagaWorker>(); // reserve/release-seats (S02)

// Contrato HTTP en snake_case ({event_id, ...}) — plan §2.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

var port = Environment.GetEnvironmentVariable("SERVICE_PORT") ?? "5080"; // D-0008: Catálogo = 5080
builder.WebHost.UseUrls($"http://localhost:{port}");

var app = builder.Build();

// Estado al arranque: tabla idempotente + seed condicional (D-0005). El estado vive en el
// emulador — reiniciar este proceso NO recrea ni pisa los datos (H-01 AC4).
var seed = SeedEvents.Parse(Environment.GetEnvironmentVariable("SEED_EVENTS"));
await app.Services.GetRequiredService<DynamoDbEventRepository>().InitializeAsync(seed, CancellationToken.None);
app.Logger.LogInformation(
    "catálogo listo en :{Port} — seed aplicado ({Count} eventos, condicional), tabla {Table}",
    port, seed.Count, DynamoDbEventRepository.TableName);

// Errores 500 sin internals (NFR-4).
app.UseExceptionHandler(errorApp => errorApp.Run(ctx =>
    Results.Problem(statusCode: 500, title: "Internal error", type: "internal-error").ExecuteAsync(ctx)));

app.MapGet("/events", async (IEventRepository repository, CancellationToken ct) =>
{
    var events = await repository.ListAsync(ct);
    return Results.Ok(events.Select(e => new EventResponse(e.Id, e.Name, e.Date, e.Price, e.Available)));
});

app.MapGet("/events/{id}/availability",
    async (HttpContext http, string id, AvailabilityService availability, CancellationToken ct) =>
    {
        if (!CatalogEventId.IsValid(id))
            return InvalidId(id);

        try
        {
            var outcome = await availability.GetAvailabilityAsync(id, ct);
            http.Response.Headers["X-Cache"] = outcome.FromCache ? "HIT" : "MISS";
            return Results.Ok(new AvailabilityResponse(id, outcome.Available));
        }
        catch (EventNotFoundException)
        {
            return Results.Problem(statusCode: 404, title: "Event not found", type: "event-not-found",
                detail: $"El evento '{id}' no existe.");
        }
    });

// Invalidación de cache expuesta para el futuro worker de compras (S02: reserve/release-seats
// muta disponibilidad e invalida en el mismo proceso) y para demos/tests (H-01 AC2, NFR-8).
// Idempotente: invalidar una key sin entrada no tiene efecto.
app.MapPost("/internal/events/{id}/invalidate-cache",
    async (string id, AvailabilityService availability, CancellationToken ct) =>
    {
        if (!CatalogEventId.IsValid(id))
            return InvalidId(id);
        await availability.InvalidateAsync(id, ct);
        return Results.NoContent();
    });

// Resiliencia observable (D-0012): inyección de fallos en vivo + DLQ del worker de este servicio.
app.MapResilienceAdmin(SagaQueues.CatalogCommands);

app.Run();

static IResult InvalidId(string id) => Results.Problem(
    statusCode: 400, title: "Invalid event id", type: "invalid-event-id",
    detail: $"El id de evento '{id}' no es válido (kebab-case, máx 64 caracteres).");

public partial class Program { }

public sealed record EventResponse(string Id, string Name, DateTimeOffset Date, decimal Price, int Available);

public sealed record AvailabilityResponse(string EventId, int Available);
