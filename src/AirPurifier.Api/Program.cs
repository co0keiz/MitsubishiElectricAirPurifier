using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AirPurifier.Api;
using AirPurifier.Core;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["Urls"]))
    builder.WebHost.UseUrls("http://localhost:5080");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiDocumentTransformer>());
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<AutoMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<ManualSpeed>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter<PurifierOperatingMode>(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOptions<PurifierOptions>().BindConfiguration("Purifier").ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PurifierOptions>, PurifierOptionsValidator>();
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    UseProxy = false,
    AllowAutoRedirect = false
}) { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<IPurifierClient>(services => new PurifierClient(
    services.GetRequiredService<HttpClient>(),
    services.GetRequiredService<IOptions<PurifierOptions>>().Value));

var app = builder.Build();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var apiKey = app.Configuration["Api:ApiKey"];
    if (context.Request.Path.StartsWithSegments("/api") && !string.IsNullOrEmpty(apiKey) &&
        !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(context.Request.Headers["X-Api-Key"].ToString()),
            Encoding.UTF8.GetBytes(apiKey)))
    {
        await Results.Problem(statusCode: 401, title: "API key required",
            detail: "Supply the configured API key in the X-Api-Key header.").ExecuteAsync(context);
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithSummary("Check that the API is running; does not contact the purifier.");
app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("Mitsubishi Electric Air Purifier API"));
var purifier = app.MapGroup("/api/purifier").WithTags("Purifier");

purifier.MapGet("/status", async (IPurifierClient client, CancellationToken cancellationToken) =>
    Results.Ok(await client.GetStatusAsync(cancellationToken)))
    .WithName("GetPurifierStatus")
    .WithSummary("Read fresh status from the configured purifier.")
    .Produces<PurifierState>().ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);

purifier.MapPut("/power", async (PowerRequest request, IPurifierClient client, CancellationToken cancellationToken) =>
{
    if (request.On is not { } on)
        return Results.Problem(statusCode: 400, title: "Power is required", detail: "Supply a JSON boolean: {\"on\":true} or {\"on\":false}.");
    return Results.Ok(await client.SetPowerAsync(on, cancellationToken));
})
    .WithName("SetPurifierPower")
    .WithSummary("Set power and wait for separate status confirmation.")
    .Produces<PurifierState>().ProducesProblem(400).ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);

purifier.MapPut("/auto-mode", async (AutoModeRequest request, IPurifierClient client, CancellationToken cancellationToken) =>
{
    if (request.Mode is not { } mode)
        return Results.Problem(statusCode: 400, title: "Auto mode is required", detail: "Supply mode: normal, night, or turbo.");
    return Results.Ok(await client.SetAutoModeAsync(mode, cancellationToken));
})
    .WithName("SetPurifierAutoMode")
    .WithSummary("Select normal, night, or turbo while the purifier is ON, and verify by readback.")
    .Produces<PurifierState>().ProducesProblem(400).ProducesProblem(409)
    .ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);

purifier.MapPut("/manual-speed", async (ManualSpeedRequest request, IPurifierClient client, CancellationToken cancellationToken) =>
{
    if (request.Speed is not { } speed)
        return Results.Problem(statusCode: 400, title: "Manual speed is required", detail: "Supply speed: silent, low, mid, high, or turbo.");
    return Results.Ok(await client.SetManualSpeedAsync(speed, cancellationToken));
})
    .WithName("SetPurifierManualSpeed")
    .WithSummary("Select silent, low, mid, high, or turbo manual operation while ON, and verify by readback.")
    .Produces<PurifierState>().ProducesProblem(400).ProducesProblem(409)
    .ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);

purifier.MapPost("/auto-cleaning", async (IPurifierClient client, CancellationToken cancellationToken) =>
    Results.Ok(await client.StartAutoCleaningAsync(cancellationToken)))
    .WithName("StartPurifierAutoCleaning")
    .WithSummary("Start pre-filter auto cleaning while ON, or return its current state if already active.")
    .WithDescription("Confirms cleaning has started through separate status readback; does not wait for the cycle to finish.")
    .Produces<PurifierState>().ProducesProblem(409)
    .ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);

purifier.MapPut("/smart-search", async (SmartSearchRequest request, IPurifierClient client, CancellationToken cancellationToken) =>
{
    if (request.Enabled is not { } enabled)
        return Results.Problem(statusCode: 400, title: "Smart Search setting is required",
            detail: "Supply a JSON boolean: {\"enabled\":true} or {\"enabled\":false}.");
    return Results.Ok(await client.SetSmartSearchAsync(enabled, cancellationToken));
})
    .WithName("SetPurifierSmartSearch")
    .WithSummary("Turn Smart Search ON or OFF while the purifier is ON, and verify by separate readback.")
    .Produces<PurifierState>().ProducesProblem(400).ProducesProblem(409)
    .ProducesProblem(502).ProducesProblem(503).ProducesProblem(504);

app.Run();

public sealed record PowerRequest(bool? On);
public sealed record AutoModeRequest(AutoMode? Mode);
public sealed record ManualSpeedRequest(ManualSpeed? Speed);
public sealed record SmartSearchRequest(bool? Enabled);

// Exposes the entry point to WebApplicationFactory for HTTP integration tests.
public partial class Program;
