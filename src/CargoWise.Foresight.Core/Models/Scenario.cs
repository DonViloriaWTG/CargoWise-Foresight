namespace CargoWise.Foresight.Core.Models;

public sealed record Scenario
{
    public required string ScenarioId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required SimulationRequest Request { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
