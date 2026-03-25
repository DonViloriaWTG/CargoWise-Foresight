using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Interfaces;

public interface IMitigationService
{
    Task<List<RiskFlag>> EnhanceMitigationsAsync(
        List<RiskFlag> risks,
        SimulationRequest request,
        CancellationToken ct = default);

    Task<List<Recommendation>> EnhanceRecommendationsAsync(
        List<Recommendation> recommendations,
        SimulationRequest request,
        SimulationResult result,
        CancellationToken ct = default);
}
