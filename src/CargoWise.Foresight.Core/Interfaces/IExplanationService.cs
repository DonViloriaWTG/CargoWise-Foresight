using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Interfaces;

public interface IExplanationService
{
    Task<ExplanationResponse> ExplainAsync(ExplanationRequest request, CancellationToken ct = default);
}
