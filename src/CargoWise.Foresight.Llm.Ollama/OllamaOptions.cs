namespace CargoWise.Foresight.Llm.Ollama;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "phi3:mini";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxRetries { get; set; } = 2;
    public int CircuitBreakerThreshold { get; set; } = 3;
    public int CircuitBreakerResetSeconds { get; set; } = 60;
}
