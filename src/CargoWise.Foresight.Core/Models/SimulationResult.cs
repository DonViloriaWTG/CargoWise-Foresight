namespace CargoWise.Foresight.Core.Models;

public sealed record SimulationResult
{
    public required string RequestId { get; init; }
    public required SimulationSummary Summary { get; init; }
    public required DistributionSet Distributions { get; init; }
    public required List<RiskFlag> Risks { get; init; }
    public required List<Recommendation> Recommendations { get; init; }
    public SimulationTraces? Traces { get; init; }
    public BaselineComparison? Baseline { get; init; }
}

public sealed record SimulationSummary
{
    public required string Outcome { get; init; }
    public required double OverallRiskScore { get; init; } // 0.0 - 1.0
    public required int SimulationRuns { get; init; }
    public required int Seed { get; init; }
    public required double DurationMs { get; init; }
}

public sealed record DistributionSet
{
    public Distribution? EtaDays { get; init; }
    public Distribution? CostUsd { get; init; }
    public Distribution? SlaBreachProbability { get; init; }
    public Distribution? MarginPercent { get; init; }
    public Distribution? WinProbability { get; init; }
}

public sealed record Distribution
{
    public required double P50 { get; init; }
    public required double P80 { get; init; }
    public required double P95 { get; init; }
    public required double Mean { get; init; }
    public required double StdDev { get; init; }
    public required List<HistogramBucket> Histogram { get; init; }
}

public sealed record HistogramBucket
{
    public required double LowerBound { get; init; }
    public required double UpperBound { get; init; }
    public required int Count { get; init; }
}

public sealed record RiskFlag
{
    public required string Type { get; init; }
    public required double Probability { get; init; }
    public required string Severity { get; init; } // "Low", "Medium", "High", "Critical"
    public required string RationaleFacts { get; init; }
    public List<string> Mitigations { get; init; } = [];
}

public sealed record Recommendation
{
    public required string Option { get; init; }
    public required string Description { get; init; }
    public Dictionary<string, double> ExpectedDeltas { get; init; } = [];
    public double Confidence { get; init; }
}

public sealed record SimulationTraces
{
    public List<string> Steps { get; init; } = [];
    public Dictionary<string, object> InternalState { get; init; } = [];
}

public sealed record BaselineComparison
{
    public required DistributionSet Distributions { get; init; }
    public required Dictionary<string, double> Deltas { get; init; }
}
