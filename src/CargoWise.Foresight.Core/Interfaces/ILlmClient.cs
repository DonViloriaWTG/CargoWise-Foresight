namespace CargoWise.Foresight.Core.Interfaces;

public interface ILlmClient
{
    Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
