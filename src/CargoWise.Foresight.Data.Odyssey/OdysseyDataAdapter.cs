using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CargoWise.Foresight.Data.Odyssey;

/// <summary>
/// Data adapter backed by the ODYSSEY (CargoWise) database.
/// Queries real shipment history when available (JobActualTransportRouting,
/// JobCharge, CusEntryHeader) and falls back to geographic estimates when not.
/// </summary>
public sealed class OdysseyDataAdapter : IDataAdapter
{
    private const int MinSampleSize = 10;

    private readonly string _connectionString;
    private readonly ILogger<OdysseyDataAdapter> _logger;

    // Cached reference data
    private readonly Lazy<Task<Dictionary<string, PortRecord>>> _ports;
    private readonly Lazy<Task<Dictionary<string, CarrierRecord>>> _carriers;
    private readonly Lazy<Task<Dictionary<string, CountryRecord>>> _countries;

    // Cached historical statistics (computed once from shipment data)
    private readonly Lazy<Task<bool>> _hasShipmentData;
    private readonly Lazy<Task<Dictionary<string, CarrierStats>>> _carrierStats;
    private readonly Lazy<Task<Dictionary<string, RouteStats>>> _routeStats;
    private readonly Lazy<Task<Dictionary<string, CustomsStats>>> _customsStats;
    private readonly Lazy<Task<Dictionary<string, CostStats>>> _costStats;

    public OdysseyDataAdapter(IOptions<OdysseyOptions> options, ILogger<OdysseyDataAdapter> logger)
    {
        _connectionString = options.Value.ConnectionString;
        _logger = logger;
        _ports = new Lazy<Task<Dictionary<string, PortRecord>>>(LoadPortsAsync);
        _carriers = new Lazy<Task<Dictionary<string, CarrierRecord>>>(LoadCarriersAsync);
        _countries = new Lazy<Task<Dictionary<string, CountryRecord>>>(LoadCountriesAsync);
        _hasShipmentData = new Lazy<Task<bool>>(CheckShipmentDataAsync);
        _carrierStats = new Lazy<Task<Dictionary<string, CarrierStats>>>(LoadCarrierStatsAsync);
        _routeStats = new Lazy<Task<Dictionary<string, RouteStats>>>(LoadRouteStatsAsync);
        _customsStats = new Lazy<Task<Dictionary<string, CustomsStats>>>(LoadCustomsStatsAsync);
        _costStats = new Lazy<Task<Dictionary<string, CostStats>>>(LoadCostStatsAsync);
    }

    public async Task<CarrierPrior?> GetCarrierPriorAsync(string carrierCode, string mode, CancellationToken ct = default)
    {
        var carriers = await _carriers.Value;
        var code = carrierCode.Trim();

        if (!carriers.TryGetValue(code, out var carrier))
        {
            _logger.LogDebug("Carrier '{CarrierCode}' not found in ODYSSEY", carrierCode);
            return null;
        }

        // Try historical stats first
        var stats = await _carrierStats.Value;
        var key = $"{code}|{mode}".ToUpperInvariant();

        if (stats.TryGetValue(key, out var historical))
        {
            _logger.LogDebug("Using historical stats for carrier {Carrier}/{Mode}: {Samples} samples", code, mode, historical.SampleCount);
            return new CarrierPrior
            {
                CarrierCode = carrier.Code.Trim(),
                Mode = mode,
                ReliabilityScore = historical.ReliabilityScore,
                MeanDelayDays = historical.MeanDelayDays,
                DelayStdDev = historical.DelayStdDev,
                BaseCostPerUnit = await GetHistoricalCostOrEstimate(mode)
            };
        }

        // Fall back to estimates
        var est = EstimateCarrierStats(mode);
        return new CarrierPrior
        {
            CarrierCode = carrier.Code.Trim(),
            Mode = mode,
            ReliabilityScore = est.Reliability,
            MeanDelayDays = est.MeanDelay,
            DelayStdDev = est.DelayStdDev,
            BaseCostPerUnit = est.BaseCost
        };
    }

    public async Task<RoutePrior?> GetRoutePriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        var ports = await _ports.Value;
        var originUp = origin.ToUpperInvariant();
        var destUp = destination.ToUpperInvariant();

        if (!ports.ContainsKey(originUp))
            _logger.LogWarning("Origin port '{Origin}' not found in ODYSSEY RefUNLOCO", origin);
        if (!ports.ContainsKey(destUp))
            _logger.LogWarning("Destination port '{Destination}' not found in ODYSSEY RefUNLOCO", destination);

        // Try historical route data first
        var routes = await _routeStats.Value;
        var key = $"{originUp}|{destUp}|{mode}".ToUpperInvariant();

        if (routes.TryGetValue(key, out var historical))
        {
            _logger.LogDebug("Using historical route stats for {Origin}->{Dest}/{Mode}: {Samples} samples",
                originUp, destUp, mode, historical.SampleCount);
            return new RoutePrior
            {
                Origin = originUp,
                Destination = destUp,
                Mode = mode,
                BaseTransitDays = historical.MeanTransitDays,
                TransitStdDev = historical.TransitStdDev,
                PortCongestionProbability = historical.CongestionProbability,
                PortCongestionDelayMean = historical.CongestionDelayMean,
                PortCongestionDelayStdDev = historical.CongestionDelayStdDev
            };
        }

        // Fall back to geographic estimates
        var originCountry = originUp.Length >= 2 ? originUp[..2] : "";
        var destCountry = destUp.Length >= 2 ? destUp[..2] : "";
        var est = EstimateTransitDays(originCountry, destCountry, mode);

        return new RoutePrior
        {
            Origin = originUp,
            Destination = destUp,
            Mode = mode,
            BaseTransitDays = est.TransitDays,
            TransitStdDev = est.StdDev,
            PortCongestionProbability = est.CongestionProb,
            PortCongestionDelayMean = est.CongestionDelayMean,
            PortCongestionDelayStdDev = est.CongestionDelayStdDev
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

        // Try historical customs data first
        var customs = await _customsStats.Value;
        if (customs.TryGetValue(code, out var historical))
        {
            _logger.LogDebug("Using historical customs stats for {Country}: {Samples} entries", code, historical.TotalEntries);
            return new CustomsPrior
            {
                Country = code,
                BaseHoldProbability = historical.HoldProbability,
                HazmatHoldMultiplier = countryRecord.IsSanctioned ? 5.0 : 3.0,
                HoldDelayMeanDays = historical.MeanHoldDays,
                HoldDelayStdDev = historical.HoldStdDev,
                HighRiskCommodities = GetHighRiskCommodities(code),
                HighRiskCommodityMultiplier = countryRecord.IsSanctioned ? 3.0 : 2.0
            };
        }

        // Fall back to estimates using sanctioned status
        var baseProbability = countryRecord.IsSanctioned ? 0.30 : 0.06;
        var holdDelay = countryRecord.IsSanctioned ? 10.0 : 4.0;
        var holdStdDev = countryRecord.IsSanctioned ? 5.0 : 2.5;

        return new CustomsPrior
        {
            Country = code,
            BaseHoldProbability = baseProbability,
            HazmatHoldMultiplier = countryRecord.IsSanctioned ? 5.0 : 3.0,
            HoldDelayMeanDays = holdDelay,
            HoldDelayStdDev = holdStdDev,
            HighRiskCommodities = GetHighRiskCommodities(code),
            HighRiskCommodityMultiplier = countryRecord.IsSanctioned ? 3.0 : 2.0
        };
    }

    public Task<DemurragePrior?> GetDemurragePriorAsync(string mode, CancellationToken ct = default)
    {
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

    public Task<RatePrior?> GetRatePriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        // TODO: Query historical JobCharge data for rate benchmarks on this lane
        var prior = new RatePrior
        {
            Origin = origin.ToUpperInvariant(),
            Destination = destination.ToUpperInvariant(),
            Mode = mode
        };
        return Task.FromResult<RatePrior?>(prior);
    }

    public Task<QuotationPrior?> GetQuotationPriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        // TODO: Query historical quotation win/loss data for this lane
        var prior = new QuotationPrior
        {
            Origin = origin.ToUpperInvariant(),
            Destination = destination.ToUpperInvariant(),
            Mode = mode
        };
        return Task.FromResult<QuotationPrior?>(prior);
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

    /// <summary>Returns summary counts of reference and historical data from ODYSSEY.</summary>
    public async Task<OdysseyStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var ports = await _ports.Value;
            var carriers = await _carriers.Value;
            var countries = await _countries.Value;
            var hasHistory = await _hasShipmentData.Value;
            var carrierHistCount = hasHistory ? (await _carrierStats.Value).Count : 0;
            var routeHistCount = hasHistory ? (await _routeStats.Value).Count : 0;
            var customsHistCount = hasHistory ? (await _customsStats.Value).Count : 0;
            var dbName = new SqlConnectionStringBuilder(_connectionString).InitialCatalog;
            return new OdysseyStatus(true, dbName, ports.Count, carriers.Count, countries.Count,
                hasHistory, carrierHistCount, routeHistCount, customsHistCount);
        }
        catch
        {
            return new OdysseyStatus(false, string.Empty, 0, 0, 0, false, 0, 0, 0);
        }
    }

    public async Task<IReadOnlyList<PortInfo>> SearchPortsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        var ports = await _ports.Value;
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Array.Empty<PortInfo>();

        var q = query.Trim().ToUpperInvariant();
        return ports.Values
            .Where(p => p.Code.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Code.StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Code)
            .Take(limit)
            .Select(p => new PortInfo(p.Code, p.Name, p.Country))
            .ToList();
    }

    public async Task<IReadOnlyList<CarrierInfo>> GetCarrierListAsync(CancellationToken ct = default)
    {
        var carriers = await _carriers.Value;
        return carriers.Values
            .OrderBy(c => c.Name)
            .Select(c => new CarrierInfo(c.Code.Trim(), c.Name))
            .ToList();
    }

    #region Reference data loaders

    private async Task<Dictionary<string, PortRecord>> LoadPortsAsync()
    {
        var ports = new Dictionary<string, PortRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT RL_Code, RL_PortName, RL_RN_NKCountryCode FROM RefUNLOCO WHERE RL_IsActive = 1", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0).Trim();
                var name = reader.GetString(1).Trim();
                var country = reader.GetString(2).Trim();
                if (!string.IsNullOrEmpty(code))
                    ports[code] = new PortRecord(code, name, country);
            }

            _logger.LogInformation("Loaded {Count} active ports from ODYSSEY", ports.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ports from ODYSSEY");
        }

        return ports;
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

    #region Historical data loaders

    private async Task<bool> CheckShipmentDataAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM JobActualTransportRouting) THEN 1 ELSE 0 END", conn);
            var result = await cmd.ExecuteScalarAsync();
            var has = Convert.ToInt32(result) == 1;
            _logger.LogInformation("ODYSSEY shipment history: {Status}", has ? "AVAILABLE" : "EMPTY (using estimates)");
            return has;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check shipment data availability");
            return false;
        }
    }

    /// <summary>
    /// Computes carrier delay statistics from JobActualTransportRouting.
    /// Delay = DATEDIFF(day, JAT_ScheduleDate, JAT_ArrivalDate) - DATEDIFF(day, JAT_ScheduleDate, JAT_DepartureDate)
    /// i.e., how many days late the arrival was vs the scheduled transit.
    /// </summary>
    private async Task<Dictionary<string, CarrierStats>> LoadCarrierStatsAsync()
    {
        var result = new Dictionary<string, CarrierStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasShipmentData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    RTRIM(JAT_Carrier) AS Carrier,
                    JAT_TransportMode AS Mode,
                    COUNT(*) AS SampleCount,
                    AVG(CAST(DATEDIFF(day, JAT_ScheduleDate, JAT_ArrivalDate) AS float)
                      - CAST(DATEDIFF(day, JAT_ScheduleDate, JAT_DepartureDate) AS float)) AS MeanDelayDays,
                    STDEV(CAST(DATEDIFF(day, JAT_ScheduleDate, JAT_ArrivalDate) AS float)
                        - CAST(DATEDIFF(day, JAT_ScheduleDate, JAT_DepartureDate) AS float)) AS DelayStdDev,
                    CAST(SUM(CASE WHEN JAT_ArrivalDate <= DATEADD(day, 1, JAT_ScheduleDate) THEN 1 ELSE 0 END) AS float)
                        / COUNT(*) AS OnTimeRatio
                FROM JobActualTransportRouting
                WHERE JAT_Carrier IS NOT NULL AND JAT_Carrier <> ''
                  AND JAT_ScheduleDate IS NOT NULL
                  AND JAT_DepartureDate IS NOT NULL
                  AND JAT_ArrivalDate IS NOT NULL
                  AND JAT_ArrivalDate > JAT_DepartureDate
                GROUP BY RTRIM(JAT_Carrier), JAT_TransportMode
                HAVING COUNT(*) >= @minSamples", conn);
            cmd.Parameters.AddWithValue("@minSamples", MinSampleSize);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var carrier = reader.GetString(0).Trim();
                var mode = reader.GetString(1).Trim();
                var count = reader.GetInt32(2);
                var meanDelay = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
                var stdDev = reader.IsDBNull(4) ? 1.0 : reader.GetDouble(4);
                var onTime = reader.IsDBNull(5) ? 0.85 : reader.GetDouble(5);

                var modeNorm = NormalizeTransportMode(mode);
                var key = $"{carrier}|{modeNorm}";
                result[key] = new CarrierStats(count, Math.Max(0, meanDelay), Math.Max(0.1, stdDev), Math.Clamp(onTime, 0, 1));
            }

            _logger.LogInformation("Computed historical carrier stats for {Count} carrier/mode combinations", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute carrier stats from ODYSSEY");
        }

        return result;
    }

    /// <summary>
    /// Computes route transit statistics from JobActualTransportRouting.
    /// Transit = DATEDIFF(day, JAT_DepartureDate, JAT_ArrivalDate) per load/discharge port pair.
    /// Congestion = proportion of legs where actual > scheduled + 1 day.
    /// </summary>
    private async Task<Dictionary<string, RouteStats>> LoadRouteStatsAsync()
    {
        var result = new Dictionary<string, RouteStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasShipmentData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    RTRIM(JAT_RL_NKLoadPort) AS LoadPort,
                    RTRIM(JAT_RL_NKDischargePort) AS DischargePort,
                    JAT_TransportMode AS Mode,
                    COUNT(*) AS SampleCount,
                    AVG(CAST(DATEDIFF(day, JAT_DepartureDate, JAT_ArrivalDate) AS float)) AS MeanTransitDays,
                    STDEV(CAST(DATEDIFF(day, JAT_DepartureDate, JAT_ArrivalDate) AS float)) AS TransitStdDev,
                    -- Congestion: legs where arrival was > 1 day later than scheduled
                    CAST(SUM(CASE WHEN DATEDIFF(day, JAT_ScheduleDate, JAT_ArrivalDate) > 
                         DATEDIFF(day, JAT_ScheduleDate, JAT_DepartureDate) + 1 THEN 1 ELSE 0 END) AS float)
                        / COUNT(*) AS CongestionProb,
                    -- Mean congestion delay (only for delayed legs)
                    AVG(CASE WHEN DATEDIFF(day, JAT_ScheduleDate, JAT_ArrivalDate) >
                         DATEDIFF(day, JAT_ScheduleDate, JAT_DepartureDate) + 1
                         THEN CAST(DATEDIFF(day, JAT_ScheduleDate, JAT_ArrivalDate) 
                            - DATEDIFF(day, JAT_ScheduleDate, JAT_DepartureDate) AS float) END) AS CongestionDelayMean
                FROM JobActualTransportRouting
                WHERE JAT_RL_NKLoadPort IS NOT NULL AND JAT_RL_NKLoadPort <> ''
                  AND JAT_RL_NKDischargePort IS NOT NULL AND JAT_RL_NKDischargePort <> ''
                  AND JAT_DepartureDate IS NOT NULL
                  AND JAT_ArrivalDate IS NOT NULL
                  AND JAT_ArrivalDate > JAT_DepartureDate
                GROUP BY RTRIM(JAT_RL_NKLoadPort), RTRIM(JAT_RL_NKDischargePort), JAT_TransportMode
                HAVING COUNT(*) >= @minSamples", conn);
            cmd.Parameters.AddWithValue("@minSamples", MinSampleSize);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var loadPort = reader.GetString(0).Trim();
                var dischPort = reader.GetString(1).Trim();
                var mode = reader.GetString(2).Trim();
                var count = reader.GetInt32(3);
                var meanTransit = reader.IsDBNull(4) ? 14.0 : reader.GetDouble(4);
                var transitStdDev = reader.IsDBNull(5) ? 3.0 : reader.GetDouble(5);
                var congProb = reader.IsDBNull(6) ? 0.15 : reader.GetDouble(6);
                var congDelay = reader.IsDBNull(7) ? 2.0 : reader.GetDouble(7);

                var modeNorm = NormalizeTransportMode(mode);
                var key = $"{loadPort}|{dischPort}|{modeNorm}";
                result[key] = new RouteStats(count, Math.Max(1, meanTransit), Math.Max(0.5, transitStdDev),
                    Math.Clamp(congProb, 0, 1), Math.Max(0, congDelay), Math.Max(0.5, congDelay * 0.5));
            }

            _logger.LogInformation("Computed historical route stats for {Count} port-pair/mode combinations", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute route stats from ODYSSEY");
        }

        return result;
    }

    /// <summary>
    /// Computes customs hold statistics from CusEntryHeader.
    /// Hold probability = entries where (ReleaseDate - SubmittedDate) > 1 day / total entries.
    /// Hold delay = mean days between submission and release for held entries.
    /// Groups by destination country (derived from the port of arrival via JobDeclaration).
    /// </summary>
    private async Task<Dictionary<string, CustomsStats>> LoadCustomsStatsAsync()
    {
        var result = new Dictionary<string, CustomsStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasShipmentData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    LEFT(RTRIM(je.JE_RL_NKPortOfArrival), 2) AS DestCountry,
                    COUNT(*) AS TotalEntries,
                    CAST(SUM(CASE WHEN DATEDIFF(day, ch.CH_EntrySubmittedDate, ch.CH_EntryReleaseDate) > 1 
                         THEN 1 ELSE 0 END) AS float) / COUNT(*) AS HoldProbability,
                    AVG(CASE WHEN DATEDIFF(day, ch.CH_EntrySubmittedDate, ch.CH_EntryReleaseDate) > 1
                         THEN CAST(DATEDIFF(day, ch.CH_EntrySubmittedDate, ch.CH_EntryReleaseDate) AS float) END) AS MeanHoldDays,
                    STDEV(CASE WHEN DATEDIFF(day, ch.CH_EntrySubmittedDate, ch.CH_EntryReleaseDate) > 1
                          THEN CAST(DATEDIFF(day, ch.CH_EntrySubmittedDate, ch.CH_EntryReleaseDate) AS float) END) AS HoldStdDev
                FROM CusEntryHeader ch
                INNER JOIN JobDeclaration je ON ch.CH_JE = je.JE_PK
                WHERE ch.CH_EntrySubmittedDate IS NOT NULL
                  AND ch.CH_EntryReleaseDate IS NOT NULL
                  AND ch.CH_EntryReleaseDate >= ch.CH_EntrySubmittedDate
                  AND je.JE_RL_NKPortOfArrival IS NOT NULL AND je.JE_RL_NKPortOfArrival <> ''
                GROUP BY LEFT(RTRIM(je.JE_RL_NKPortOfArrival), 2)
                HAVING COUNT(*) >= @minSamples", conn);
            cmd.Parameters.AddWithValue("@minSamples", MinSampleSize);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var country = reader.GetString(0).Trim().ToUpperInvariant();
                var total = reader.GetInt32(1);
                var holdProb = reader.IsDBNull(2) ? 0.06 : reader.GetDouble(2);
                var meanHold = reader.IsDBNull(3) ? 4.0 : reader.GetDouble(3);
                var holdStd = reader.IsDBNull(4) ? 2.5 : reader.GetDouble(4);

                result[country] = new CustomsStats(total, Math.Clamp(holdProb, 0, 1),
                    Math.Max(0, meanHold), Math.Max(0.5, holdStd));
            }

            _logger.LogInformation("Computed historical customs stats for {Count} countries", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute customs stats from ODYSSEY");
        }

        return result;
    }

    /// <summary>
    /// Computes average cost per shipment from JobCharge, grouped by transport mode.
    /// Path: JobCharge (JR_JH) -> JobHeader (JH_PK) -> transport mode + cost sum.
    /// </summary>
    private async Task<Dictionary<string, CostStats>> LoadCostStatsAsync()
    {
        var result = new Dictionary<string, CostStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasShipmentData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    RTRIM(jh.JH_TransportMode) AS Mode,
                    COUNT(DISTINCT jr.JR_JH) AS ShipmentCount,
                    AVG(jr.JR_OSCostAmt) AS MeanCostPerCharge,
                    SUM(jr.JR_OSCostAmt) / NULLIF(COUNT(DISTINCT jr.JR_JH), 0) AS MeanCostPerShipment
                FROM JobCharge jr
                INNER JOIN JobHeader jh ON jr.JR_JH = jh.JH_PK
                WHERE jr.JR_OSCostAmt > 0
                  AND jh.JH_TransportMode IS NOT NULL AND jh.JH_TransportMode <> ''
                GROUP BY RTRIM(jh.JH_TransportMode)
                HAVING COUNT(DISTINCT jr.JR_JH) >= @minSamples", conn);
            cmd.Parameters.AddWithValue("@minSamples", MinSampleSize);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var mode = reader.GetString(0).Trim();
                var shipCount = reader.GetInt32(1);
                var meanPerCharge = reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2));
                var meanPerShip = reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader.GetValue(3));

                var modeNorm = NormalizeTransportMode(mode);
                result[modeNorm] = new CostStats(shipCount, meanPerShip);
            }

            _logger.LogInformation("Computed historical cost stats for {Count} transport modes", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute cost stats from ODYSSEY");
        }

        return result;
    }

    private async Task<double> GetHistoricalCostOrEstimate(string mode)
    {
        var costs = await _costStats.Value;
        var modeNorm = NormalizeTransportMode(mode);
        if (costs.TryGetValue(modeNorm, out var c))
            return c.MeanCostPerShipment;
        return EstimateCarrierStats(mode).BaseCost;
    }

    #endregion

    #region Estimation fallbacks

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
            "ROAD" => region switch
            {
                RegionDistance.Local => (3.0, 1.0, 0.08, 1.0, 0.5),
                RegionDistance.Regional => (7.0, 2.0, 0.10, 1.5, 1.0),
                _ => (30.0, 10.0, 0.25, 3.0, 2.0)  // Intercontinental road is infeasible; high values signal this
            },
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

        return originContinent == destContinent ? RegionDistance.Regional : RegionDistance.Intercontinental;
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

    /// <summary>
    /// Maps CargoWise transport mode codes (SEA, AIR, ROA, RAI, etc.) to Foresight modes.
    /// </summary>
    private static string NormalizeTransportMode(string cwMode) => cwMode.Trim().ToUpperInvariant() switch
    {
        "SEA" or "FCL" or "LCL" => "Ocean",
        "AIR" => "Air",
        "ROA" or "ROD" => "Road",
        "RAI" or "RAL" => "Rail",
        _ => cwMode.Trim()
    };

    #endregion

    private enum RegionDistance { Local, Regional, Intercontinental }
    internal sealed record PortRecord(string Code, string Name, string Country);
    internal sealed record CarrierRecord(string Code, string Name);
    internal sealed record CountryRecord(string Code, string Description, bool IsSanctioned, string Currency);
    internal sealed record CarrierStats(int SampleCount, double MeanDelayDays, double DelayStdDev, double ReliabilityScore);
    internal sealed record RouteStats(int SampleCount, double MeanTransitDays, double TransitStdDev,
        double CongestionProbability, double CongestionDelayMean, double CongestionDelayStdDev);
    internal sealed record CustomsStats(int TotalEntries, double HoldProbability, double MeanHoldDays, double HoldStdDev);
    internal sealed record CostStats(int ShipmentCount, double MeanCostPerShipment);
}

public sealed record OdysseyStatus(bool Connected, string DatabaseName, int PortCount, int CarrierCount, int CountryCount,
    bool HasShipmentHistory, int CarrierStatCount, int RouteStatCount, int CustomsStatCount);
