using System.Text.Json;
using System.Text.Json.Serialization;
using CargoWise.Foresight.Api.Middleware;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Services;
using CargoWise.Foresight.Core.Simulation;
using CargoWise.Foresight.Data.Mock;
using CargoWise.Foresight.Data.Odyssey;
using CargoWise.Foresight.Data.Softship;
using CargoWise.Foresight.Llm.Ollama;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title = "CargoWise Foresight — What-If Engine API",
        Version = "v2",
        Description = "Read-only simulation & decision AI for logistics what-if analysis. " +
                       "Runs sandboxed Monte Carlo simulations to predict outcomes before changes are committed."
    });
});

// Core services — data source selection
builder.Services.AddHttpContextAccessor();
var dataSource = builder.Configuration.GetValue<string>("DataSource") ?? "Mock";
var softshipConnStr = builder.Configuration.GetSection("Softship")?.GetValue<string>("ConnectionString");

// Primary data adapter (Odyssey or Mock)
if (dataSource.Equals("Odyssey", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.Configure<OdysseyOptions>(builder.Configuration.GetSection("Odyssey"));
    builder.Services.AddSingleton<OdysseyDataAdapter>();
}
else
{
    builder.Services.AddSingleton<MockDataAdapter>();
}

// Softship data adapter (optional, for Softship GUI)
if (!string.IsNullOrWhiteSpace(softshipConnStr))
{
    builder.Services.Configure<SoftshipOptions>(builder.Configuration.GetSection("Softship"));
    builder.Services.AddSingleton<SoftshipDataAdapter>();
}

// Wire IDataAdapter via proxy that selects the right adapter per request
builder.Services.AddSingleton<IDataAdapter>(sp =>
{
    IDataAdapter primary = dataSource.Equals("Odyssey", StringComparison.OrdinalIgnoreCase)
        ? sp.GetRequiredService<OdysseyDataAdapter>()
        : sp.GetRequiredService<MockDataAdapter>();

    IDataAdapter? softship = !string.IsNullOrWhiteSpace(softshipConnStr)
        ? sp.GetRequiredService<SoftshipDataAdapter>()
        : null;

    var httpCtx = sp.GetRequiredService<IHttpContextAccessor>();
    return new DataAdapterProxy(httpCtx, primary, softship);
});
builder.Services.AddSingleton<ISimulationEngine, MonteCarloSimulationEngine>();
builder.Services.AddSingleton<IExplanationService, ExplanationService>();

// Ollama LLM
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddSingleton<ILlmClient, OllamaLlmClient>();

// Observability
builder.Services.AddSingleton<MetricsCollector>();

var app = builder.Build();

app.UseMiddleware<RequestLoggingMiddleware>();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "What-If Engine v2");
    c.RoutePrefix = "swagger";
});

app.MapControllers();

// Reference data endpoints
app.MapGet("/api/ports", async (string? q, IDataAdapter data, CancellationToken ct) =>
{
    var results = await data.SearchPortsAsync(q ?? "", 20, ct);
    return Results.Ok(results);
});

app.MapGet("/api/carriers", async (IDataAdapter data, CancellationToken ct) =>
{
    var results = await data.GetCarrierListAsync(ct);
    return Results.Ok(results);
});

// Health endpoint
app.MapGet("/health", async (ILlmClient llm, IDataAdapter data, IOptions<OllamaOptions> ollamaOpts, IConfiguration config, IServiceProvider sp, CancellationToken ct) =>
{
    var opts = ollamaOpts.Value;
    var ollamaClient = llm as OllamaLlmClient;
    var running = false;
    var models = Array.Empty<string>();
    var modelReady = false;

    if (ollamaClient is not null)
    {
        (running, models) = await ollamaClient.GetStatusAsync(ct);
        modelReady = models.Any(m =>
            m.Equals(opts.Model, StringComparison.OrdinalIgnoreCase) ||
            m.StartsWith(opts.Model + ":", StringComparison.OrdinalIgnoreCase));
    }

    string status;
    if (!running) status = "offline";
    else if (!modelReady) status = "model_missing";
    else status = "ready";

    var dataSourceName = config.GetValue<string>("DataSource") ?? "Mock";
    object? dataStatus = null;
    var odyssey = sp.GetService<OdysseyDataAdapter>();
    if (odyssey is not null)
    {
        var odyStatus = await odyssey.GetStatusAsync(ct);
        dataSourceName = "Odyssey";
        dataStatus = new
        {
            connected = odyStatus.Connected,
            ports = odyStatus.PortCount,
            carriers = odyStatus.CarrierCount,
            countries = odyStatus.CountryCount,
            hasShipmentHistory = odyStatus.HasShipmentHistory,
            historicalStats = new
            {
                carrierRoutes = odyStatus.CarrierStatCount,
                portPairs = odyStatus.RouteStatCount,
                customsCountries = odyStatus.CustomsStatCount
            }
        };
    }

    object? softshipStatus = null;
    var softship = sp.GetService<SoftshipDataAdapter>();
    if (softship is not null)
    {
        var ssStatus = await softship.GetStatusAsync(ct);
        softshipStatus = new
        {
            connected = ssStatus.Connected,
            ports = ssStatus.PortCount,
            carriers = ssStatus.CarrierCount,
            countries = ssStatus.CountryCount,
            hasFileHistory = ssStatus.HasFileHistory,
            historicalStats = new
            {
                carrierRoutes = ssStatus.CarrierStatCount,
                portPairs = ssStatus.RouteStatCount,
                customsCountries = ssStatus.CustomsStatCount
            }
        };
    }

    return Results.Ok(new
    {
        status = "healthy",
        service = "cargowise-foresight",
        version = "2.0.0-mvp",
        timestamp = DateTimeOffset.UtcNow,
        dataSource = dataSourceName,
        odyssey = dataStatus,
        softship = softshipStatus,
        ollama = new
        {
            available = modelReady,
            running,
            status,
            configuredModel = opts.Model,
            availableModels = models
        }
    });
}).WithTags("Health");

// Metrics endpoint
app.MapGet("/metrics", (MetricsCollector metrics) => Results.Ok(metrics.GetSnapshot()))
    .WithTags("Observability");

app.Run();

// Make Program accessible for integration tests
public partial class Program { }
