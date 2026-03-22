using System.Collections.Concurrent;

namespace CargoWise.Foresight.Api.Middleware;

public sealed class MetricsCollector
{
    private long _totalSimulations;
    private long _successfulSimulations;
    private long _failedSimulations;
    private long _totalDurationMs;
    private readonly ConcurrentQueue<SimulationMetricEntry> _recentEntries = new();
    private const int MaxRecentEntries = 100;

    public void RecordSimulation(string requestId, TimeSpan duration, bool success)
    {
        Interlocked.Increment(ref _totalSimulations);
        Interlocked.Add(ref _totalDurationMs, (long)duration.TotalMilliseconds);

        if (success)
            Interlocked.Increment(ref _successfulSimulations);
        else
            Interlocked.Increment(ref _failedSimulations);

        _recentEntries.Enqueue(new SimulationMetricEntry
        {
            RequestId = requestId,
            DurationMs = duration.TotalMilliseconds,
            Success = success,
            Timestamp = DateTimeOffset.UtcNow
        });

        // Trim
        while (_recentEntries.Count > MaxRecentEntries)
            _recentEntries.TryDequeue(out _);
    }

    public MetricsSnapshot GetSnapshot()
    {
        long total = Interlocked.Read(ref _totalSimulations);
        long durationMs = Interlocked.Read(ref _totalDurationMs);

        return new MetricsSnapshot
        {
            TotalSimulations = total,
            SuccessfulSimulations = Interlocked.Read(ref _successfulSimulations),
            FailedSimulations = Interlocked.Read(ref _failedSimulations),
            AverageDurationMs = total > 0 ? (double)durationMs / total : 0,
            RecentEntries = _recentEntries.ToArray()
        };
    }
}

public sealed record MetricsSnapshot
{
    public long TotalSimulations { get; init; }
    public long SuccessfulSimulations { get; init; }
    public long FailedSimulations { get; init; }
    public double AverageDurationMs { get; init; }
    public SimulationMetricEntry[] RecentEntries { get; init; } = [];
}

public sealed record SimulationMetricEntry
{
    public required string RequestId { get; init; }
    public required double DurationMs { get; init; }
    public required bool Success { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
