namespace CargoWise.Foresight.Core.Models;

public sealed record ExplanationRequest
{
    public required string RequestId { get; init; }
    public required SimulationResult SimulationResult { get; init; }
    public string Audience { get; init; } = "operator"; // "operator", "manager", "customer"
    public string Tone { get; init; } = "professional";
}

public sealed record ExplanationResponse
{
    public required string RequestId { get; init; }
    public required string Narrative { get; init; }
    public required List<string> KeyDrivers { get; init; }
    public required List<string> Assumptions { get; init; }
    public required List<string> Caveats { get; init; }
    public bool GeneratedByLlm { get; init; }
}
