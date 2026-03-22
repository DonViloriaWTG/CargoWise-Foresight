using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Interfaces;

public interface IDataAdapter
{
    Task<CarrierPrior?> GetCarrierPriorAsync(string carrierCode, string mode, CancellationToken ct = default);
    Task<RoutePrior?> GetRoutePriorAsync(string origin, string destination, string mode, CancellationToken ct = default);
    Task<CustomsPrior?> GetCustomsPriorAsync(string country, CancellationToken ct = default);
    Task<DemurragePrior?> GetDemurragePriorAsync(string mode, CancellationToken ct = default);
}
