using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Services;

public sealed class InMemoryKnowledgeStore : IKnowledgeStore
{
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILogger<InMemoryKnowledgeStore> _logger;
    private readonly ConcurrentDictionary<string, KnowledgeChunk> _chunks = new();
    private readonly string? _filePath;
    private readonly object _fileLock = new();

    private static readonly JsonSerializerOptions FileJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public InMemoryKnowledgeStore(IEmbeddingClient embeddingClient, ILogger<InMemoryKnowledgeStore> logger, string? filePath = null)
    {
        _embeddingClient = embeddingClient;
        _logger = logger;
        _filePath = filePath;

        if (_filePath is not null)
            LoadFromFile();
    }

    public async Task IngestAsync(KnowledgeChunk chunk, CancellationToken ct = default)
    {
        var stored = chunk.Embedding is not null
            ? chunk
            : chunk with { Embedding = await _embeddingClient.GenerateEmbeddingAsync(chunk.Content, ct) };

        _chunks[stored.Id] = stored;
        _logger.LogInformation("Ingested knowledge chunk {ChunkId} in category {Category}", stored.Id, stored.Category);
        SaveToFile();
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
        SaveToFile();
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
        SaveToFile();
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

    private void LoadFromFile()
    {
        if (_filePath is null || !File.Exists(_filePath)) return;

        try
        {
            var json = File.ReadAllText(_filePath);
            var chunks = JsonSerializer.Deserialize<List<KnowledgeChunk>>(json, FileJsonOpts);
            if (chunks is null) return;

            foreach (var chunk in chunks)
                _chunks[chunk.Id] = chunk;

            _logger.LogInformation("Loaded {Count} knowledge chunks from {FilePath}", chunks.Count, _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load knowledge store from {FilePath}, starting empty", _filePath);
        }
    }

    private void SaveToFile()
    {
        if (_filePath is null) return;

        try
        {
            lock (_fileLock)
            {
                var dir = Path.GetDirectoryName(_filePath);
                if (dir is not null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(_chunks.Values.ToList(), FileJsonOpts);
                File.WriteAllText(_filePath, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save knowledge store to {FilePath}", _filePath);
        }
    }
}
