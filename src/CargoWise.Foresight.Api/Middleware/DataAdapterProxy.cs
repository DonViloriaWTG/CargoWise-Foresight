using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Api.Middleware;

/// <summary>
/// Proxy that routes IDataAdapter calls to the correct backend (Odyssey or Softship)
/// based on the X-Data-Source request header. Falls back to the default (primary) adapter
/// when no header is present or when the Softship adapter is not configured.
/// </summary>
public sealed class DataAdapterProxy : IDataAdapter
{
    private const string HeaderName = "X-Data-Source";

    private readonly IHttpContextAccessor _httpCtx;
    private readonly IDataAdapter _primary;
    private readonly IDataAdapter? _softship;

    public DataAdapterProxy(IHttpContextAccessor httpCtx, IDataAdapter primary, IDataAdapter? softship)
    {
        _httpCtx = httpCtx;
        _primary = primary;
        _softship = softship;
    }

    private IDataAdapter Resolve()
    {
        var header = _httpCtx.HttpContext?.Request.Headers[HeaderName].FirstOrDefault();
        if (string.Equals(header, "Softship", StringComparison.OrdinalIgnoreCase) && _softship is not null)
            return _softship;
        return _primary;
    }

    public Task<CarrierPrior?> GetCarrierPriorAsync(string carrierCode, string mode, CancellationToken ct = default)
        => Resolve().GetCarrierPriorAsync(carrierCode, mode, ct);

    public Task<RoutePrior?> GetRoutePriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
        => Resolve().GetRoutePriorAsync(origin, destination, mode, ct);

    public Task<CustomsPrior?> GetCustomsPriorAsync(string country, CancellationToken ct = default)
        => Resolve().GetCustomsPriorAsync(country, ct);

    public Task<DemurragePrior?> GetDemurragePriorAsync(string mode, CancellationToken ct = default)
        => Resolve().GetDemurragePriorAsync(mode, ct);

    public Task<IReadOnlyList<PortInfo>> SearchPortsAsync(string query, int limit = 20, CancellationToken ct = default)
        => Resolve().SearchPortsAsync(query, limit, ct);

    public Task<IReadOnlyList<CarrierInfo>> GetCarrierListAsync(CancellationToken ct = default)
        => Resolve().GetCarrierListAsync(ct);
}
