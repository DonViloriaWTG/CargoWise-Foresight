using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;
using Microsoft.Extensions.Logging;

namespace CargoWise.Foresight.Core.Services;

public static class KnowledgeSeeder
{
    public static async Task SeedAsync(IKnowledgeStore store, ILogger logger, CancellationToken ct = default)
    {
        var existing = await store.CountAsync();
        if (existing > 0)
        {
            logger.LogInformation("Knowledge store already has {Count} chunks, skipping seed", existing);
            return;
        }

        logger.LogInformation("No verified startup knowledge is bundled with the application; knowledge store will start empty.");

        var chunks = GetSeedChunks();
        await store.IngestManyAsync(chunks, ct);

        logger.LogInformation("Seeded {Count} knowledge chunks", chunks.Count);
    }

    private static List<KnowledgeChunk> GetSeedChunks() => [];
}
