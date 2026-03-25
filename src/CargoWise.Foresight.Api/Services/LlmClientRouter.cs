using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Llm.Ollama;
using CargoWise.Foresight.Llm.GitHubModels;
using Microsoft.Extensions.Logging;

namespace CargoWise.Foresight.Api.Services;

public sealed class LlmClientRouter : ILlmClient, IEmbeddingClient
{
    private readonly LlmProviderSettings _settings;
    private readonly OllamaLlmClient _ollama;
    private readonly GitHubModelsLlmClient? _githubModels;
    private readonly ILogger<LlmClientRouter> _logger;

    public LlmClientRouter(
        LlmProviderSettings settings,
        OllamaLlmClient ollama,
        GitHubModelsLlmClient? githubModels,
        ILogger<LlmClientRouter> logger)
    {
        _settings = settings;
        _ollama = ollama;
        _githubModels = githubModels;
        _logger = logger;
    }

    public OllamaLlmClient Ollama => _ollama;
    public GitHubModelsLlmClient? GitHubModels => _githubModels;

    private ILlmClient GetActiveClient()
    {
        var (provider, model) = _settings.Current;

        if (provider.Equals("GitHubModels", StringComparison.OrdinalIgnoreCase) && _githubModels is not null)
        {
            var token = _settings.Token;
            if (!string.IsNullOrEmpty(token))
                _githubModels.UpdateToken(token);
            // If no session token but client has one from config/user-secrets, use it as-is
            _githubModels.ModelOverride = model;
            return _githubModels;
        }

        _ollama.ModelOverride = model;
        return _ollama;
    }

    private IEmbeddingClient GetActiveEmbeddingClient()
    {
        var (provider, _) = _settings.Current;

        if (provider.Equals("GitHubModels", StringComparison.OrdinalIgnoreCase) && _githubModels is not null)
        {
            var token = _settings.Token;
            if (!string.IsNullOrEmpty(token))
                _githubModels.UpdateToken(token);
            return _githubModels;
        }

        return _ollama;
    }

    public Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var (provider, model) = _settings.Current;
        _logger.LogInformation("Routing LLM request to {Provider} model={Model}", provider, model);
        return GetActiveClient().GenerateAsync(systemPrompt, userPrompt, ct);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return GetActiveClient().IsAvailableAsync(ct);
    }

    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
    {
        var (provider, _) = _settings.Current;
        _logger.LogInformation("Routing embedding request to {Provider}", provider);
        return GetActiveEmbeddingClient().GenerateEmbeddingAsync(text, ct);
    }

    Task<bool> IEmbeddingClient.IsAvailableAsync(CancellationToken ct)
    {
        return GetActiveEmbeddingClient().IsAvailableAsync(ct);
    }
}
