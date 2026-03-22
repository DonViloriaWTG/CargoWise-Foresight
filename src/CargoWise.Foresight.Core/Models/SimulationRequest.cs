namespace CargoWise.Foresight.Core.Models;

public sealed record SimulationRequest
{
    public required string RequestId { get; init; }
    public int Seed { get; init; } = 42;
    public required BaselineSnapshot Baseline { get; init; }
    public required ChangeSet ChangeSet { get; init; }
    public int HorizonDays { get; init; } = 30;
    public int SimulationRuns { get; init; } = 500;
    public SimulationObjectives? Objectives { get; init; }
}

public sealed record SimulationObjectives
{
    public bool IncludeCost { get; init; } = true;
    public bool IncludeEta { get; init; } = true;
    public bool IncludeSlaRisk { get; init; } = true;
    public bool IncludeComplianceRisk { get; init; } = true;
}
