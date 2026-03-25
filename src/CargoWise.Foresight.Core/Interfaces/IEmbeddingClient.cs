namespace CargoWise.Foresight.Core.Interfaces;

public interface IEmbeddingClient
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
    Task<bool> IsAvailableAsync(CancellationToken ct = default);
}
