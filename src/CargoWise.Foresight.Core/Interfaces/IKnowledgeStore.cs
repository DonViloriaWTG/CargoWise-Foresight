using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Interfaces;

public interface IKnowledgeStore
{
    Task IngestAsync(KnowledgeChunk chunk, CancellationToken ct = default);
    Task IngestManyAsync(IEnumerable<KnowledgeChunk> chunks, CancellationToken ct = default);
    Task<IReadOnlyList<RetrievalResult>> SearchAsync(string query, int topK = 3, double minSimilarity = 0.3, CancellationToken ct = default);
    Task<IReadOnlyList<RetrievalResult>> SearchByEmbeddingAsync(float[] queryEmbedding, int topK = 3, double minSimilarity = 0.3);
    Task<int> CountAsync();
    Task<IReadOnlyList<KnowledgeChunk>> ListAsync(string? category = null);
    Task RemoveAsync(string chunkId);
    Task ReEmbedAllAsync(CancellationToken ct = default);
}
