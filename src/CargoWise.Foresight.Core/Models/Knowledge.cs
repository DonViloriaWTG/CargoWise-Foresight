namespace CargoWise.Foresight.Core.Models;

public sealed record KnowledgeChunk
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public float[]? Embedding { get; init; }
}

public sealed record RetrievalResult
{
    public required KnowledgeChunk Chunk { get; init; }
    public required double Similarity { get; init; }
}
