using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Interfaces;

public interface ISimulationEngine
{
    Task<SimulationResult> RunAsync(SimulationRequest request, CancellationToken ct = default);
}
