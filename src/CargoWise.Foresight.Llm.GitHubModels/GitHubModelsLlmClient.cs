using System.ClientModel;
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using CargoWise.Foresight.Core.Interfaces;

namespace CargoWise.Foresight.Llm.GitHubModels;

public sealed class GitHubModelsLlmClient : ILlmClient
{
    private readonly GitHubModelsOptions _options;
    private readonly ILogger<GitHubModelsLlmClient> _logger;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();
    private readonly object _tokenLock = new();
    private string _activeToken = "";

    // Runtime model override (set by router for model switching)
    public volatile string? ModelOverride;

    private string ActiveModel => ModelOverride ?? _options.Model;

    public static readonly string[] KnownModels =
    [
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4.1",
        "gpt-4.1-mini",
        "gpt-4.1-nano",
        "o3-mini",
        "o4-mini",
        "Mistral-Large-2411",
        "Meta-Llama-3.1-405B-Instruct",
        "DeepSeek-R1"
    ];

    public GitHubModelsLlmClient(IOptions<GitHubModelsOptions> options, ILogger<GitHubModelsLlmClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _activeToken = _options.Token;
    }

    public void UpdateToken(string token)
    {
        lock (_tokenLock)
        {
            if (_activeToken == token) return;
            _activeToken = token;
            _clients.Clear(); // rebuild clients with new credential
        }
    }

    public bool HasToken
    {
        get { lock (_tokenLock) return !string.IsNullOrEmpty(_activeToken); }
    }

    private IChatClient? GetClient(string model)
    {
        string token;
        lock (_tokenLock) { token = _activeToken; }

        if (string.IsNullOrEmpty(token))
            return null;

        return _clients.GetOrAdd(model, m =>
            new OpenAIClient(
                    new ApiKeyCredential(token),
                    new OpenAIClientOptions { Endpoint = new Uri("https://models.inference.ai.azure.com") })
                .GetChatClient(m)
                .AsIChatClient());
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!HasToken)
            return false;

        try
        {
            var client = GetClient(ActiveModel);
            if (client is null) return false;

            var messages = new List<ChatMessage>
            {
                new(ChatRole.User, "ping")
            };

            var response = await client.GetResponseAsync(messages, cancellationToken: ct);
            return !string.IsNullOrEmpty(response.Text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub Models availability check failed");
            return false;
        }
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var model = ActiveModel;
        var client = GetClient(model)
            ?? throw new InvalidOperationException("GitHub Models token is not configured.");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        _logger.LogInformation("Sending request to GitHub Models model={Model}, promptLength={Len}",
            model, userPrompt.Length);

        var response = await client.GetResponseAsync(messages, cancellationToken: ct);

        _logger.LogInformation("GitHub Models response received, model={Model}, length={Len}",
            model, response.Text?.Length ?? 0);

        return response.Text ?? "";
    }
}
