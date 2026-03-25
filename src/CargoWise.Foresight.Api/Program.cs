using System.Text.Json;
using System.Text.Json.Serialization;
using CargoWise.Foresight.Api.Middleware;
using CargoWise.Foresight.Api.Services;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Services;
using CargoWise.Foresight.Core.Simulation;
using CargoWise.Foresight.Data.Mock;
using CargoWise.Foresight.Data.Odyssey;
using CargoWise.Foresight.Llm.Ollama;
using CargoWise.Foresight.Llm.GitHubModels;
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
builder.Services.AddSingleton<IMitigationService, MitigationService>();

// LLM providers — register both, route via LlmClientRouter
builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.AddSingleton<OllamaLlmClient>();
builder.Services.Configure<GitHubModelsOptions>(builder.Configuration.GetSection("GitHubModels"));
builder.Services.AddSingleton<GitHubModelsLlmClient>();

var llmProvider = builder.Configuration.GetValue<string>("LlmProvider") ?? "Ollama";
var llmModel = llmProvider.Equals("GitHubModels", StringComparison.OrdinalIgnoreCase)
    ? builder.Configuration.GetValue<string>("GitHubModels:Model") ?? "gpt-4o"
    : builder.Configuration.GetValue<string>("Ollama:Model") ?? "phi3:mini";
builder.Services.AddSingleton(new LlmProviderSettings(llmProvider, llmModel));
builder.Services.AddSingleton<LlmClientRouter>(sp => new LlmClientRouter(
    sp.GetRequiredService<LlmProviderSettings>(),
    sp.GetRequiredService<OllamaLlmClient>(),
    sp.GetService<GitHubModelsLlmClient>(),
    sp.GetRequiredService<ILogger<LlmClientRouter>>()));
builder.Services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<LlmClientRouter>());
builder.Services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<LlmClientRouter>());

// RAG — knowledge store
builder.Services.AddSingleton<IKnowledgeStore, InMemoryKnowledgeStore>();

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

// Seed knowledge store with domain knowledge
_ = Task.Run(async () =>
{
    try
    {
        var knowledgeStore = app.Services.GetRequiredService<IKnowledgeStore>();
        var seedLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KnowledgeSeeder");
        await KnowledgeSeeder.SeedAsync(knowledgeStore, seedLogger);
    }
    catch (Exception ex)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("KnowledgeSeeder");
        logger.LogWarning(ex, "Knowledge seeding failed — RAG will operate without seed data until embeddings are available");
    }
});

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
app.MapGet("/health", async (LlmClientRouter router, LlmProviderSettings llmSettings, IDataAdapter data, IKnowledgeStore knowledgeStore, IConfiguration config, CancellationToken ct) =>
{
    var dataSourceName = config.GetValue<string>("DataSource") ?? "Mock";
    object? dataStatus = null;
    if (data is OdysseyDataAdapter odyssey)
    {
        var odyStatus = await odyssey.GetStatusAsync(ct);
        dataSourceName = "Odyssey";
        dataStatus = new
        {
            connected = odyStatus.Connected,
            databaseName = odyStatus.DatabaseName,
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

    // LLM provider status
    var (activeProvider, activeModel) = llmSettings.Current;

    var (ollamaRunning, ollamaModels) = await router.Ollama.GetStatusAsync(ct);

    var ghConfigured = router.GitHubModels?.HasToken ?? false;
    var knowledgeCount = await knowledgeStore.CountAsync();

    return Results.Ok(new
    {
        status = "healthy",
        service = "cargowise-foresight",
        version = "2.0.0-mvp",
        timestamp = DateTimeOffset.UtcNow,
        dataSource = dataSourceName,
        odyssey = dataStatus,
        rag = new { knowledgeChunks = knowledgeCount },
        llm = new
        {
            activeProvider,
            activeModel,
            providers = new object[]
            {
                new
                {
                    name = "Ollama",
                    running = ollamaRunning,
                    models = ollamaModels
                },
                new
                {
                    name = "GitHubModels",
                    configured = ghConfigured,
                    models = GitHubModelsLlmClient.KnownModels
                }
            }
        }
    });
}).WithTags("Health");

// LLM settings endpoints
app.MapGet("/api/llm/settings", (LlmProviderSettings settings, LlmClientRouter router) =>
{
    var (provider, model) = settings.Current;
    var hasToken = !string.IsNullOrEmpty(settings.Token) || (router.GitHubModels?.HasToken ?? false);
    return Results.Ok(new { provider, model, hasToken });
}).WithTags("LLM");

app.MapPut("/api/llm/settings", async (LlmSettingsRequest body, LlmProviderSettings settings, LlmClientRouter router, IKnowledgeStore knowledgeStore, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(body.Provider) || string.IsNullOrWhiteSpace(body.Model))
        return Results.BadRequest(new { error = "provider and model are required" });

    if (body.Provider is not ("Ollama" or "GitHubModels"))
        return Results.BadRequest(new { error = "provider must be 'Ollama' or 'GitHubModels'" });

    var (previousProvider, _) = settings.Current;

    settings.Update(body.Provider, body.Model, body.Token);

    // Push token to GitHubModels client immediately
    if (body.Token is not null && router.GitHubModels is not null)
        router.GitHubModels.UpdateToken(body.Token);

    logger.LogInformation("LLM settings updated: provider={Provider}, model={Model}", body.Provider, body.Model);

    // Re-embed knowledge chunks if the embedding provider changed
    if (!previousProvider.Equals(body.Provider, StringComparison.OrdinalIgnoreCase))
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await knowledgeStore.ReEmbedAllAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background re-embedding failed after provider switch to {Provider}", body.Provider);
            }
        });
    }

    return Results.Ok(new { provider = body.Provider, model = body.Model });
}).WithTags("LLM");

app.MapGet("/api/llm/providers", async (LlmClientRouter router, CancellationToken ct) =>
{
    var (ollamaRunning, ollamaModels) = await router.Ollama.GetStatusAsync(ct);
    var ghConfigured = router.GitHubModels?.HasToken ?? false;

    return Results.Ok(new[]
    {
        new
        {
            name = "Ollama",
            available = ollamaRunning,
            models = ollamaModels
        },
        new
        {
            name = "GitHubModels",
            available = ghConfigured,
            models = GitHubModelsLlmClient.KnownModels
        }
    });
}).WithTags("LLM");

// Metrics endpoint
app.MapGet("/metrics", (MetricsCollector metrics) => Results.Ok(metrics.GetSnapshot()))
    .WithTags("Observability");

app.Run();

// Make Program accessible for integration tests
public partial class Program { }

internal sealed record LlmSettingsRequest(string Provider, string Model, string? Token = null);
