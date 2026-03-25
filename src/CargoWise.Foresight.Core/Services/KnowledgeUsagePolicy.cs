using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Services;

public static class KnowledgeUsagePolicy
{
    public const string VerifiedKey = "verified";
    public const string SourceKey = "source";
    public const string AsOfUtcKey = "asOfUtc";
    public const string SourceUrlKey = "sourceUrl";

    // Only data from these organisations can influence LLM prompts.
    // Each entry is a canonical short-name matched case-insensitively against the
    // "source" metadata value on each KnowledgeChunk.
    // To add a new approved source, add a single entry here — no other code changes needed.
    private static readonly HashSet<string> ApprovedSourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Government / customs authorities
        "CBP",                      // U.S. Customs and Border Protection (rulings, CSMS alerts)
        "Australian Border Force",  // ABF import alerts & prohibited goods
        "EU TARIC",                 // EU tariff/trade database
        "WCO",                      // World Customs Organization HS classification

        // Port authorities & AIS data
        "Port of Los Angeles",      // POLA monthly TEU reports
        "Port of Long Beach",       // POLB monthly stats
        "MarineTraffic",            // AIS-based vessel tracking & port congestion

        // Freight rate indices (public tiers)
        "Freightos",                // Freightos Baltic Index (FBX) — publicly available
        "Drewry",                   // Drewry WCI composite (headline index is public)
        "Xeneta",                   // If licensed; public market commentary

        // Industry research & multilateral data
        "Sea-Intelligence",         // Schedule reliability reports
        "UNCTAD",                   // UN trade & maritime review (free)
        "World Bank",               // Logistics Performance Index, trade indicators
        "IMF",                      // Exchange-rate and trade-flow data
        "WTO",                      // Trade statistics & tariff profiles

        // Regulatory / safety
        "IMDG",                     // International Maritime Dangerous Goods code
        "IATA DGR",                 // IATA Dangerous Goods Regulations

        // Internal verified data
        "CargoWise Foresight"       // Curated data verified by the Foresight team
    };

    /// <summary>
    /// A chunk is eligible for RAG only when ALL of:
    ///   1. verified = true
    ///   2. source is non-empty
    ///   3. source matches the approved-source allowlist
    /// </summary>
    public static bool IsVerifiedForRag(KnowledgeChunk chunk)
    {
        if (chunk.Metadata is null || chunk.Metadata.Count == 0)
            return false;

        return chunk.Metadata.TryGetValue(VerifiedKey, out var verified)
            && bool.TryParse(verified, out var isVerified)
            && isVerified
            && chunk.Metadata.TryGetValue(SourceKey, out var source)
            && !string.IsNullOrWhiteSpace(source)
            && IsApprovedSource(source);
    }

    public static bool IsApprovedSource(string source)
        => !string.IsNullOrWhiteSpace(source) && ApprovedSourceNames.Contains(source.Trim());

    public static IReadOnlyList<string> GetApprovedSources()
        => ApprovedSourceNames.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool TryGetAsOfUtc(KnowledgeChunk chunk, out DateTimeOffset asOfUtc)
    {
        asOfUtc = default;

        return chunk.Metadata is not null
            && chunk.Metadata.TryGetValue(AsOfUtcKey, out var value)
            && DateTimeOffset.TryParse(value, out asOfUtc);
    }

    public static string BuildFreshnessDisclaimer(IReadOnlyList<KnowledgeChunk> chunks)
    {
        if (chunks.Count == 0)
            return "No verified external reference context was used in this explanation.";

        var sources = chunks
            .Select(chunk => chunk.Metadata.TryGetValue(SourceKey, out var source) ? source : null)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dates = chunks
            .Select(chunk => TryGetAsOfUtc(chunk, out var asOfUtc) ? asOfUtc.UtcDateTime.Date : (DateTime?)null)
            .Where(date => date.HasValue)
            .Select(date => date!.Value)
            .OrderBy(date => date)
            .ToList();

        var sourceLabel = sources.Count switch
        {
            0 => "verified reference sources",
            1 => sources[0]!,
            _ => string.Join(", ", sources)
        };

        if (dates.Count == 0)
            return $"Verified reference context was used from {sourceLabel}, but no as-of date was provided for that source material.";

        var oldest = dates.First();
        var newest = dates.Last();
        if (oldest == newest)
            return $"Verified reference context was used from {sourceLabel}, current as of {newest:yyyy-MM-dd}.";

        return $"Verified reference context was used from {sourceLabel}, spanning source dates {oldest:yyyy-MM-dd} to {newest:yyyy-MM-dd}.";
    }
}