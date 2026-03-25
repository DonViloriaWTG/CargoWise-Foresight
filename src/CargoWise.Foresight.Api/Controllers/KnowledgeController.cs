using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;
using CargoWise.Foresight.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace CargoWise.Foresight.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public sealed class KnowledgeController : ControllerBase
{
    private readonly IKnowledgeStore _store;
    private readonly ILogger<KnowledgeController> _logger;

    public KnowledgeController(IKnowledgeStore store, ILogger<KnowledgeController> logger)
    {
        _store = store;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? category)
    {
        var chunks = await _store.ListAsync(category);
        return Ok(chunks.Select(c => new
        {
            c.Id,
            c.Category,
            c.Title,
            contentLength = c.Content.Length,
            c.Metadata,
            verifiedForRag = KnowledgeUsagePolicy.IsVerifiedForRag(c),
            approvedSource = c.Metadata.TryGetValue(KnowledgeUsagePolicy.SourceKey, out var src) && KnowledgeUsagePolicy.IsApprovedSource(src)
        }));
    }

    [HttpGet("count")]
    public async Task<IActionResult> Count()
    {
        var count = await _store.CountAsync();
        return Ok(new { count });
    }

    [HttpGet("approved-sources")]
    public IActionResult ApprovedSources()
    {
        return Ok(new
        {
            description = "Only knowledge chunks whose 'source' metadata matches one of these names (case-insensitive) can be used in LLM prompts. Chunks from other sources are stored but never influence explanations or mitigations.",
            sources = KnowledgeUsagePolicy.GetApprovedSources()
        });
    }

    [HttpPost]
    public async Task<IActionResult> Ingest([FromBody] KnowledgeIngestRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new ProblemDetails { Title = "title and content are required", Status = 400 });

        var chunk = new KnowledgeChunk
        {
            Id = request.Id ?? Guid.NewGuid().ToString("N")[..12],
            Category = request.Category ?? "general",
            Title = request.Title,
            Content = request.Content,
            Metadata = request.Metadata ?? []
        };

        var hasSource = chunk.Metadata.TryGetValue(KnowledgeUsagePolicy.SourceKey, out var source)
                        && !string.IsNullOrWhiteSpace(source);
        var isApproved = hasSource && KnowledgeUsagePolicy.IsApprovedSource(source!);

        await _store.IngestAsync(chunk, ct);

        string? warning = null;
        if (!hasSource)
        {
            warning = "No 'source' metadata provided. This chunk will be stored but will NOT be used in LLM prompts. Add a 'source' from the approved list (GET /api/knowledge/approved-sources).";
            _logger.LogWarning("Ingested chunk {ChunkId} has no source metadata — excluded from RAG", chunk.Id);
        }
        else if (!isApproved)
        {
            warning = $"Source '{source}' is not on the approved-source list. This chunk will be stored but will NOT be used in LLM prompts. See GET /api/knowledge/approved-sources for accepted sources.";
            _logger.LogWarning("Ingested chunk {ChunkId} has unapproved source '{Source}' — excluded from RAG", chunk.Id, source);
        }
        else
        {
            _logger.LogInformation("Ingested knowledge chunk {ChunkId}: {Title} (source: {Source})", chunk.Id, chunk.Title, source);
        }

        return Created($"/api/knowledge/{chunk.Id}", new
        {
            chunk.Id,
            chunk.Category,
            chunk.Title,
            verifiedForRag = KnowledgeUsagePolicy.IsVerifiedForRag(chunk),
            approvedSource = isApproved,
            warning
        });
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] KnowledgeSearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(new ProblemDetails { Title = "query is required", Status = 400 });

        var results = await _store.SearchAsync(
            request.Query,
            request.TopK ?? 3,
            request.MinSimilarity ?? 0.3,
            ct);

        return Ok(results.Select(r => new
        {
            r.Chunk.Id,
            r.Chunk.Category,
            r.Chunk.Title,
            r.Chunk.Content,
            r.Chunk.Metadata,
            verifiedForRag = KnowledgeUsagePolicy.IsVerifiedForRag(r.Chunk),
            approvedSource = r.Chunk.Metadata.TryGetValue(KnowledgeUsagePolicy.SourceKey, out var src) && KnowledgeUsagePolicy.IsApprovedSource(src),
            r.Similarity
        }));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Remove(string id)
    {
        await _store.RemoveAsync(id);
        return NoContent();
    }
}

public sealed record KnowledgeIngestRequest
{
    public string? Id { get; init; }
    public string? Category { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}

public sealed record KnowledgeSearchRequest
{
    public required string Query { get; init; }
    public int? TopK { get; init; }
    public double? MinSimilarity { get; init; }
}
