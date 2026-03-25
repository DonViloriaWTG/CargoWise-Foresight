using System.Collections.Concurrent;
using System.Numerics;
using Microsoft.Extensions.Logging;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Services;

public sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILogger<InMemoryKnowledgeStore> _logger;
    private readonly ConcurrentDictionary<string, KnowledgeChunk> _chunks = new();

    public InMemoryKnowledgeStore(IEmbeddingClient embeddingClient, ILogger<InMemoryKnowledgeStore> logger)
    {
        _embeddingClient = embeddingClient;
        _logger = logger;
    }

    public async Task IngestAsync(KnowledgeChunk chunk, CancellationToken ct = default)
    {
        var stored = chunk.Embedding is not null
            ? chunk
            : chunk with { Embedding = await _embeddingClient.GenerateEmbeddingAsync(chunk.Content, ct) };

        _chunks[stored.Id] = stored;
        _logger.LogInformation("Ingested knowledge chunk {ChunkId} in category {Category}", stored.Id, stored.Category);
    }

    public async Task IngestManyAsync(IEnumerable<KnowledgeChunk> chunks, CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            await IngestAsync(chunk, ct);
        }
    }

    public async Task<IReadOnlyList<RetrievalResult>> SearchAsync(
        string query, int topK = 3, double minSimilarity = 0.3, CancellationToken ct = default)
    {
        if (_chunks.IsEmpty) return [];

        var queryEmbedding = await _embeddingClient.GenerateEmbeddingAsync(query, ct);
        return await SearchByEmbeddingAsync(queryEmbedding, topK, minSimilarity);
    }

    public Task<IReadOnlyList<RetrievalResult>> SearchByEmbeddingAsync(
        float[] queryEmbedding, int topK = 3, double minSimilarity = 0.3)
    {
        var results = _chunks.Values
            .Where(c => c.Embedding is not null)
            .Select(c => new RetrievalResult
            {
                Chunk = c,
                Similarity = CosineSimilarity(queryEmbedding, c.Embedding!)
            })
            .Where(r => r.Similarity >= minSimilarity)
            .OrderByDescending(r => r.Similarity)
            .Take(topK)
            .ToList();

        return Task.FromResult<IReadOnlyList<RetrievalResult>>(results);
    }

    public Task<int> CountAsync() => Task.FromResult(_chunks.Count);

    public Task<IReadOnlyList<KnowledgeChunk>> ListAsync(string? category = null)
    {
        IEnumerable<KnowledgeChunk> items = _chunks.Values;
        if (category is not null)
            items = items.Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<IReadOnlyList<KnowledgeChunk>>(items.ToList());
    }

    public Task RemoveAsync(string chunkId)
    {
        _chunks.TryRemove(chunkId, out _);
        return Task.CompletedTask;
    }

    public async Task ReEmbedAllAsync(CancellationToken ct = default)
    {
        var chunks = _chunks.Values.ToList();
        if (chunks.Count == 0) return;

        _logger.LogInformation("Re-embedding {Count} knowledge chunks with current embedding provider", chunks.Count);

        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            var newEmbedding = await _embeddingClient.GenerateEmbeddingAsync(chunk.Content, ct);
            _chunks[chunk.Id] = chunk with { Embedding = newEmbedding };
        }

        _logger.LogInformation("Re-embedding complete for {Count} chunks", chunks.Count);
    }

    internal static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return 0.0;

        // Use SIMD-accelerated dot product when possible
        var spanA = a.AsSpan();
        var spanB = b.AsSpan();

        float dot = 0f, normA = 0f, normB = 0f;

        int i = 0;
        int simdLength = Vector<float>.Count;
        int remaining = a.Length - (a.Length % simdLength);

        // Vectorized loop
        for (; i < remaining; i += simdLength)
        {
            var va = new Vector<float>(spanA.Slice(i, simdLength));
            var vb = new Vector<float>(spanB.Slice(i, simdLength));
            dot += Vector.Dot(va, vb);
            normA += Vector.Dot(va, va);
            normB += Vector.Dot(vb, vb);
        }

        // Scalar remainder
        for (; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        if (normA == 0f || normB == 0f) return 0.0;
        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
