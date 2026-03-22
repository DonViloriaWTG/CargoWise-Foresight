using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CargoWise.Foresight.Core.Interfaces;

namespace CargoWise.Foresight.Llm.Ollama;

public sealed class OllamaLlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaLlmClient> _logger;

    // Circuit breaker state
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;
    private readonly object _cbLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OllamaLlmClient(IOptions<OllamaOptions> options, ILogger<OllamaLlmClient> logger)
        : this(options.Value, logger, null) { }

    internal OllamaLlmClient(OllamaOptions options, ILogger<OllamaLlmClient> logger, HttpClient? httpClient)
    {
        _options = options;
        _logger = logger;
        _httpClient = httpClient ?? new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds)
        };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (IsCircuitOpen()) return false;

        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(JsonOpts, ct);
            if (tags?.Models is null || tags.Models.Length == 0) return false;

            // Verify the configured model is actually available
            return tags.Models.Any(m =>
                m.Name != null &&
                (m.Name.Equals(_options.Model, StringComparison.OrdinalIgnoreCase) ||
                 m.Name.StartsWith(_options.Model + ":", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns the list of model names available in Ollama, or empty if unreachable.</summary>
    public async Task<(bool running, string[] models)> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return (false, Array.Empty<string>());

            var tags = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>(JsonOpts, ct);
            var names = tags?.Models?.Select(m => m.Name ?? "").Where(n => n.Length > 0).ToArray()
                        ?? Array.Empty<string>();
            return (true, names);
        }
        catch
        {
            return (false, Array.Empty<string>());
        }
    }

    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        if (IsCircuitOpen())
        {
            throw new InvalidOperationException("Circuit breaker is open. Ollama calls are temporarily disabled.");
        }

        var request = new OllamaGenerateRequest
        {
            Model = _options.Model,
            System = systemPrompt,
            Prompt = userPrompt,
            Stream = false
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Sending generate request to Ollama model={Model}, promptLength={Len}",
            _options.Model, userPrompt.Length);

        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                var response = await _httpClient.PostAsync("/api/generate", content, ct);
                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>(JsonOpts, ct);
                RecordSuccess();

                _logger.LogInformation("Ollama response received, model={Model}, length={Len}",
                    _options.Model, result?.Response?.Length ?? 0);

                return result?.Response ?? string.Empty;
            }
            catch (Exception ex) when (attempt <= _options.MaxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Ollama attempt {Attempt}/{Max} failed", attempt, _options.MaxRetries);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
            catch (Exception ex)
            {
                RecordFailure();
                _logger.LogError(ex, "Ollama request failed after {Attempts} attempts", attempt);
                throw;
            }
        }
    }

    private bool IsCircuitOpen()
    {
        lock (_cbLock)
        {
            if (_consecutiveFailures >= _options.CircuitBreakerThreshold &&
                DateTimeOffset.UtcNow < _circuitOpenUntil)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= _circuitOpenUntil)
            {
                _consecutiveFailures = 0;
            }

            return false;
        }
    }

    private void RecordSuccess()
    {
        lock (_cbLock) { _consecutiveFailures = 0; }
    }

    private void RecordFailure()
    {
        lock (_cbLock)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= _options.CircuitBreakerThreshold)
            {
                _circuitOpenUntil = DateTimeOffset.UtcNow.AddSeconds(_options.CircuitBreakerResetSeconds);
                _logger.LogWarning("Circuit breaker opened for Ollama, resets at {ResetTime}", _circuitOpenUntil);
            }
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("system")]
        public string? System { get; set; }

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }
    }

    private sealed class OllamaGenerateResponse
    {
        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    private sealed class OllamaTagsResponse
    {
        [JsonPropertyName("models")]
        public OllamaModelInfo[]? Models { get; set; }
    }

    private sealed class OllamaModelInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
