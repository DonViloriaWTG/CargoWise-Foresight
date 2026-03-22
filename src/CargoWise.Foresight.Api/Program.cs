using System.Text.Json;
using System.Text.Json.Serialization;
using CargoWise.Foresight.Api.Middleware;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Services;
using CargoWise.Foresight.Core.Simulation;
using CargoWise.Foresight.Data.Mock;
using CargoWise.Foresight.Data.Odyssey;
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
        Version = "v1",
        Description = "Read-only simulation & decision AI for logistics what-if analysis. " +
                       "Runs sandboxed Monte Carlo simulations to predict outcomes before changes are committed."
    });
});

// Core services — data source selection
var dataSource = builder.Configuration.GetValue<string>("DataSource") ?? "Mock";
if (dataSource.Equals("Odyssey", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.Configure<OdysseyOptions>(builder.Configuration.GetSection("Odyssey"));
    builder.Services.AddSingleton<OdysseyDataAdapter>();
    builder.Services.AddSingleton<IDataAdapter>(sp => sp.GetRequiredService<OdysseyDataAdapter>());
}
else
{
    builder.Services.AddSingleton<IDataAdapter, MockDataAdapter>();
}
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
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "What-If Engine v1");
    c.RoutePrefix = "swagger";
});

app.MapControllers();

// Health endpoint
app.MapGet("/health", async (ILlmClient llm, IDataAdapter data, IOptions<OllamaOptions> ollamaOpts, IConfiguration config, CancellationToken ct) =>
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
    if (data is OdysseyDataAdapter odyssey)
    {
        var odyStatus = await odyssey.GetStatusAsync(ct);
        dataSourceName = "Odyssey";
        dataStatus = new
        {
            connected = odyStatus.Connected,
            ports = odyStatus.PortCount,
            carriers = odyStatus.CarrierCount,
            countries = odyStatus.CountryCount
        };
    }

    return Results.Ok(new
    {
        status = "healthy",
        service = "cargowise-foresight",
        version = "1.0.0-mvp",
        timestamp = DateTimeOffset.UtcNow,
        dataSource = dataSourceName,
        odyssey = dataStatus,
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
