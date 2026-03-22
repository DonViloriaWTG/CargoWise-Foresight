using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Interfaces;

public interface IDataAdapter
{
    Task<CarrierPrior?> GetCarrierPriorAsync(string carrierCode, string mode, CancellationToken ct = default);
    Task<RoutePrior?> GetRoutePriorAsync(string origin, string destination, string mode, CancellationToken ct = default);
    Task<CustomsPrior?> GetCustomsPriorAsync(string country, CancellationToken ct = default);
    Task<DemurragePrior?> GetDemurragePriorAsync(string mode, CancellationToken ct = default);

    /// <summary>Search ports by code or name prefix. Returns up to <paramref name="limit"/> matches.</summary>
    Task<IReadOnlyList<PortInfo>> SearchPortsAsync(string query, int limit = 20, CancellationToken ct = default);

    /// <summary>Returns all available carriers.</summary>
    Task<IReadOnlyList<CarrierInfo>> GetCarrierListAsync(CancellationToken ct = default);
}

public sealed record PortInfo(string Code, string Name, string Country);
public sealed record CarrierInfo(string Code, string Name);
