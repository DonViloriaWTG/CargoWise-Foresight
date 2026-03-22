using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CargoWise.Foresight.Data.Odyssey;

/// <summary>
/// Data adapter backed by the ODYSSEY (CargoWise) database.
/// Uses real reference data (ports, carriers, countries) from ODYSSEY
/// with estimated statistical distributions for delay/cost modeling.
/// When historical shipment data becomes available, distributions
/// can be computed from actual JobShipment / JobActualTransportRouting records.
/// </summary>
public sealed class OdysseyDataAdapter : IDataAdapter
{
    private readonly string _connectionString;
    private readonly ILogger<OdysseyDataAdapter> _logger;

    // Cache reference data to avoid repeated DB queries
    private readonly Lazy<Task<HashSet<string>>> _validPortCodes;
    private readonly Lazy<Task<Dictionary<string, CarrierRecord>>> _carriers;
    private readonly Lazy<Task<Dictionary<string, CountryRecord>>> _countries;

    public OdysseyDataAdapter(IOptions<OdysseyOptions> options, ILogger<OdysseyDataAdapter> logger)
    {
        _connectionString = options.Value.ConnectionString;
        _logger = logger;
        _validPortCodes = new Lazy<Task<HashSet<string>>>(LoadPortCodesAsync);
        _carriers = new Lazy<Task<Dictionary<string, CarrierRecord>>>(LoadCarriersAsync);
        _countries = new Lazy<Task<Dictionary<string, CountryRecord>>>(LoadCountriesAsync);
    }

    public async Task<CarrierPrior?> GetCarrierPriorAsync(string carrierCode, string mode, CancellationToken ct = default)
    {
        var carriers = await _carriers.Value;

        if (carriers.TryGetValue(carrierCode.Trim(), out var carrier))
        {
            // Use the real carrier name from ODYSSEY, with estimated performance stats
            var stats = EstimateCarrierStats(mode);
            return new CarrierPrior
            {
                CarrierCode = carrier.Code.Trim(),
                Mode = mode,
                ReliabilityScore = stats.Reliability,
                MeanDelayDays = stats.MeanDelay,
                DelayStdDev = stats.DelayStdDev,
                BaseCostPerUnit = stats.BaseCost
            };
        }

        _logger.LogDebug("Carrier '{CarrierCode}' not found in ODYSSEY, returning null", carrierCode);
        return null;
    }

    public async Task<RoutePrior?> GetRoutePriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        var ports = await _validPortCodes.Value;

        var originValid = ports.Contains(origin.ToUpperInvariant());
        var destValid = ports.Contains(destination.ToUpperInvariant());

        if (!originValid)
            _logger.LogWarning("Origin port '{Origin}' not found in ODYSSEY RefUNLOCO", origin);
        if (!destValid)
            _logger.LogWarning("Destination port '{Destination}' not found in ODYSSEY RefUNLOCO", destination);

        // Extract country codes from UNLOCO (first 2 chars)
        var originCountry = origin.Length >= 2 ? origin[..2].ToUpperInvariant() : "";
        var destCountry = destination.Length >= 2 ? destination[..2].ToUpperInvariant() : "";

        // Estimate transit based on mode and geography
        var transitEstimate = EstimateTransitDays(originCountry, destCountry, mode);

        return new RoutePrior
        {
            Origin = origin.ToUpperInvariant(),
            Destination = destination.ToUpperInvariant(),
            Mode = mode,
            BaseTransitDays = transitEstimate.TransitDays,
            TransitStdDev = transitEstimate.StdDev,
            PortCongestionProbability = transitEstimate.CongestionProb,
            PortCongestionDelayMean = transitEstimate.CongestionDelayMean,
            PortCongestionDelayStdDev = transitEstimate.CongestionDelayStdDev
        };
    }

    public async Task<CustomsPrior?> GetCustomsPriorAsync(string country, CancellationToken ct = default)
    {
        var countries = await _countries.Value;
        var code = country.ToUpperInvariant().Trim();

        if (!countries.TryGetValue(code, out var countryRecord))
        {
            _logger.LogDebug("Country '{Country}' not found in ODYSSEY RefCountry", country);
            return null;
        }

        // Use sanctioned status from ODYSSEY to inform customs risk
        var baseProbability = countryRecord.IsSanctioned ? 0.30 : 0.06;
        var hazmatMultiplier = countryRecord.IsSanctioned ? 5.0 : 3.0;
        var holdDelay = countryRecord.IsSanctioned ? 10.0 : 4.0;
        var holdStdDev = countryRecord.IsSanctioned ? 5.0 : 2.5;

        return new CustomsPrior
        {
            Country = code,
            BaseHoldProbability = baseProbability,
            HazmatHoldMultiplier = hazmatMultiplier,
            HoldDelayMeanDays = holdDelay,
            HoldDelayStdDev = holdStdDev,
            HighRiskCommodities = GetHighRiskCommodities(code),
            HighRiskCommodityMultiplier = countryRecord.IsSanctioned ? 3.0 : 2.0
        };
    }

    public Task<DemurragePrior?> GetDemurragePriorAsync(string mode, CancellationToken ct = default)
    {
        // Demurrage rates are not in ODYSSEY reference data — use industry estimates
        var prior = mode.ToUpperInvariant() switch
        {
            "OCEAN" => new DemurragePrior { Mode = "Ocean", FreeTimeDays = 7, DailyRate = 150 },
            "AIR" => new DemurragePrior { Mode = "Air", FreeTimeDays = 3, DailyRate = 200 },
            "ROAD" => new DemurragePrior { Mode = "Road", FreeTimeDays = 2, DailyRate = 100 },
            "RAIL" => new DemurragePrior { Mode = "Rail", FreeTimeDays = 5, DailyRate = 120 },
            _ => null
        };
        return Task.FromResult(prior);
    }

    /// <summary>Checks database connectivity.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ODYSSEY database is not available");
            return false;
        }
    }

    /// <summary>Returns summary counts of reference data loaded from ODYSSEY.</summary>
    public async Task<OdysseyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var ports = await _validPortCodes.Value;
            var carriers = await _carriers.Value;
            var countries = await _countries.Value;
            return new OdysseyStatus(true, ports.Count, carriers.Count, countries.Count);
        }
        catch
        {
            return new OdysseyStatus(false, 0, 0, 0);
        }
    }

    #region Database loaders

    private async Task<HashSet<string>> LoadPortCodesAsync()
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT RL_Code FROM RefUNLOCO WHERE RL_IsActive = 1", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0).Trim();
                if (!string.IsNullOrEmpty(code))
                    codes.Add(code);
            }

            _logger.LogInformation("Loaded {Count} active port codes from ODYSSEY", codes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load port codes from ODYSSEY");
        }

        return codes;
    }

    private async Task<Dictionary<string, CarrierRecord>> LoadCarriersAsync()
    {
        var carriers = new Dictionary<string, CarrierRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT OH_Code, OH_FullName FROM OrgHeader WHERE OH_IsShippingProvider = 1", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0).Trim();
                var name = reader.GetString(1).Trim();
                if (!string.IsNullOrEmpty(code))
                    carriers[code] = new CarrierRecord(code, name);
            }

            _logger.LogInformation("Loaded {Count} shipping providers from ODYSSEY", carriers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load carriers from ODYSSEY");
        }

        return carriers;
    }

    private async Task<Dictionary<string, CountryRecord>> LoadCountriesAsync()
    {
        var countries = new Dictionary<string, CountryRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT RN_Code, RN_Desc, RN_IsSanctioned, RN_RX_NKLocalCurrency FROM RefCountry WHERE RN_IsActive = 1",
                conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0).Trim();
                var desc = reader.GetString(1).Trim();
                var sanctioned = reader.GetBoolean(2);
                var currency = reader.GetString(3).Trim();
                if (!string.IsNullOrEmpty(code))
                    countries[code] = new CountryRecord(code, desc, sanctioned, currency);
            }

            _logger.LogInformation("Loaded {Count} countries from ODYSSEY", countries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load countries from ODYSSEY");
        }

        return countries;
    }

    #endregion

    #region Estimation helpers

    private static (double Reliability, double MeanDelay, double DelayStdDev, double BaseCost) EstimateCarrierStats(string mode)
    {
        return mode.ToUpperInvariant() switch
        {
            "OCEAN" => (0.85, 1.5, 2.0, 2000),
            "AIR" => (0.92, 0.3, 0.5, 5500),
            "ROAD" => (0.90, 0.5, 1.0, 1500),
            "RAIL" => (0.88, 0.8, 1.2, 1200),
            _ => (0.82, 2.0, 3.0, 2000)
        };
    }

    private static (double TransitDays, double StdDev, double CongestionProb, double CongestionDelayMean, double CongestionDelayStdDev)
        EstimateTransitDays(string originCountry, string destCountry, string mode)
    {
        // Rough geographic distance buckets
        var region = ClassifyRegionPair(originCountry, destCountry);

        return mode.ToUpperInvariant() switch
        {
            "AIR" => region switch
            {
                RegionDistance.Local => (0.5, 0.2, 0.03, 0.3, 0.2),
                RegionDistance.Regional => (1.0, 0.3, 0.05, 0.5, 0.3),
                RegionDistance.Intercontinental => (2.0, 0.5, 0.05, 0.5, 0.3),
                _ => (2.0, 0.5, 0.05, 0.5, 0.3)
            },
            "OCEAN" => region switch
            {
                RegionDistance.Local => (5.0, 1.5, 0.10, 1.5, 1.0),
                RegionDistance.Regional => (14.0, 3.0, 0.15, 2.5, 1.5),
                RegionDistance.Intercontinental => (28.0, 5.0, 0.20, 3.0, 2.0),
                _ => (18.0, 4.0, 0.15, 2.5, 1.5)
            },
            "ROAD" => (3.0, 1.0, 0.08, 1.0, 0.5),
            "RAIL" => region switch
            {
                RegionDistance.Local => (3.0, 1.0, 0.05, 1.0, 0.5),
                RegionDistance.Regional => (7.0, 2.0, 0.08, 1.5, 1.0),
                _ => (14.0, 4.0, 0.10, 2.0, 1.5)
            },
            _ => (14.0, 3.0, 0.15, 2.5, 1.5)
        };
    }

    private static RegionDistance ClassifyRegionPair(string originCountry, string destCountry)
    {
        if (string.Equals(originCountry, destCountry, StringComparison.OrdinalIgnoreCase))
            return RegionDistance.Local;

        var originContinent = GetContinent(originCountry);
        var destContinent = GetContinent(destCountry);

        if (originContinent == destContinent)
            return RegionDistance.Regional;

        return RegionDistance.Intercontinental;
    }

    private static string GetContinent(string countryCode) => countryCode.ToUpperInvariant() switch
    {
        "US" or "CA" or "MX" or "GT" or "BZ" or "HN" or "SV" or "NI" or "CR" or "PA" => "NAM",
        "BR" or "AR" or "CL" or "CO" or "PE" or "VE" or "EC" or "BO" or "PY" or "UY" => "SAM",
        "GB" or "DE" or "FR" or "NL" or "BE" or "IT" or "ES" or "PT" or "SE" or "NO" or
        "DK" or "FI" or "PL" or "CZ" or "AT" or "CH" or "IE" or "GR" or "RO" or "HU" or
        "BG" or "HR" or "SK" or "SI" or "LT" or "LV" or "EE" => "EUR",
        "CN" or "JP" or "KR" or "TW" or "HK" or "MO" or "MN" => "EAS",
        "IN" or "PK" or "BD" or "LK" or "NP" => "SAS",
        "SG" or "MY" or "TH" or "VN" or "PH" or "ID" or "MM" or "KH" or "LA" or "BN" => "SEA",
        "AU" or "NZ" or "FJ" or "PG" or "WS" => "OCE",
        "AE" or "SA" or "QA" or "KW" or "BH" or "OM" or "JO" or "LB" or "IL" or "TR" or "IQ" or "IR" => "MEA",
        "ZA" or "NG" or "KE" or "EG" or "MA" or "TN" or "GH" or "ET" or "TZ" or "CI" => "AFR",
        _ => "OTH"
    };

    private static List<string> GetHighRiskCommodities(string countryCode) => countryCode switch
    {
        "US" => ["electronics", "chemicals", "pharmaceuticals", "lithium_batteries"],
        "CN" => ["food", "chemicals", "cosmetics"],
        "AU" => ["food", "agriculture", "wood", "biologicals"],
        "JP" => ["food", "chemicals", "electronics"],
        "IN" => ["chemicals", "pharmaceuticals", "food"],
        _ => ["chemicals"]
    };

    #endregion

    private enum RegionDistance { Local, Regional, Intercontinental }
    internal sealed record CarrierRecord(string Code, string Name);
    internal sealed record CountryRecord(string Code, string Description, bool IsSanctioned, string Currency);
}

public sealed record OdysseyStatus(bool Connected, int PortCount, int CarrierCount, int CountryCount);
