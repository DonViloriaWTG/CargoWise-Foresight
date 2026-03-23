using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CargoWise.Foresight.Data.Softship;

/// <summary>
/// Data adapter backed by the Softship database.
/// Queries Softship reference data (Port, Carrier, Country) and shipment
/// history (FileRouting, FileCharge, CustomsEntry) for statistical priors,
/// falling back to geographic estimates when historical data is insufficient.
/// </summary>
public sealed class SoftshipDataAdapter : IDataAdapter
{
    private const int MinSampleSize = 10;

    private readonly string _connectionString;
    private readonly ILogger<SoftshipDataAdapter> _logger;

    // Cached reference data
    private readonly Lazy<Task<Dictionary<string, PortRecord>>> _ports;
    private readonly Lazy<Task<Dictionary<string, CarrierRecord>>> _carriers;
    private readonly Lazy<Task<Dictionary<string, CountryRecord>>> _countries;

    // Cached historical statistics
    private readonly Lazy<Task<bool>> _hasFileData;
    private readonly Lazy<Task<Dictionary<string, CarrierStats>>> _carrierStats;
    private readonly Lazy<Task<Dictionary<string, RouteStats>>> _routeStats;
    private readonly Lazy<Task<Dictionary<string, CustomsStats>>> _customsStats;
    private readonly Lazy<Task<Dictionary<string, CostStats>>> _costStats;

    public SoftshipDataAdapter(IOptions<SoftshipOptions> options, ILogger<SoftshipDataAdapter> logger)
    {
        _connectionString = options.Value.ConnectionString;
        _logger = logger;
        _ports = new Lazy<Task<Dictionary<string, PortRecord>>>(LoadPortsAsync);
        _carriers = new Lazy<Task<Dictionary<string, CarrierRecord>>>(LoadCarriersAsync);
        _countries = new Lazy<Task<Dictionary<string, CountryRecord>>>(LoadCountriesAsync);
        _hasFileData = new Lazy<Task<bool>>(CheckFileDataAsync);
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
            _logger.LogDebug("Carrier '{CarrierCode}' not found in Softship", carrierCode);
            return null;
        }

        // Try historical stats first
        var stats = await _carrierStats.Value;
        var key = $"{code}|{mode}".ToUpperInvariant();

        if (stats.TryGetValue(key, out var historical))
        {
            _logger.LogDebug("Using historical stats for carrier {Carrier}/{Mode}: {Samples} samples",
                code, mode, historical.SampleCount);
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
            _logger.LogWarning("Origin port '{Origin}' not found in Softship Port table", origin);
        if (!ports.ContainsKey(destUp))
            _logger.LogWarning("Destination port '{Destination}' not found in Softship Port table", destination);

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
            _logger.LogDebug("Country '{Country}' not found in Softship Country table", country);
            return null;
        }

        // Try historical customs data first
        var customs = await _customsStats.Value;
        if (customs.TryGetValue(code, out var historical))
        {
            _logger.LogDebug("Using historical customs stats for {Country}: {Samples} entries",
                code, historical.TotalEntries);
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

        // Fall back to estimates
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
            _logger.LogWarning(ex, "Softship database is not available");
            return false;
        }
    }

    /// <summary>Returns summary counts of reference and historical data from Softship.</summary>
    public async Task<SoftshipStatus> GetStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var ports = await _ports.Value;
            var carriers = await _carriers.Value;
            var countries = await _countries.Value;
            var hasHistory = await _hasFileData.Value;
            var carrierHistCount = hasHistory ? (await _carrierStats.Value).Count : 0;
            var routeHistCount = hasHistory ? (await _routeStats.Value).Count : 0;
            var customsHistCount = hasHistory ? (await _customsStats.Value).Count : 0;
            return new SoftshipStatus(true, ports.Count, carriers.Count, countries.Count,
                hasHistory, carrierHistCount, routeHistCount, customsHistCount);
        }
        catch
        {
            return new SoftshipStatus(false, 0, 0, 0, false, 0, 0, 0);
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

    #region Reference data loaders (Softship schema)

    /// <summary>
    /// Loads ports from the Softship Port table.
    /// Softship schema: Port(PortCode, PortName, CountryCode, IsActive)
    /// </summary>
    private async Task<Dictionary<string, PortRecord>> LoadPortsAsync()
    {
        var ports = new Dictionary<string, PortRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT PortCode, PortName, CountryCode FROM Port WHERE IsActive = 1", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0).Trim();
                var name = reader.GetString(1).Trim();
                var country = reader.GetString(2).Trim();
                if (!string.IsNullOrEmpty(code))
                    ports[code] = new PortRecord(code, name, country);
            }

            _logger.LogInformation("Loaded {Count} active ports from Softship", ports.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ports from Softship");
        }

        return ports;
    }

    /// <summary>
    /// Loads carriers/shipping lines from the Softship Carrier table.
    /// Softship schema: Carrier(CarrierCode, CarrierName, IsShippingLine, IsActive)
    /// </summary>
    private async Task<Dictionary<string, CarrierRecord>> LoadCarriersAsync()
    {
        var carriers = new Dictionary<string, CarrierRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT CarrierCode, CarrierName FROM Carrier WHERE IsShippingLine = 1 AND IsActive = 1", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0).Trim();
                var name = reader.GetString(1).Trim();
                if (!string.IsNullOrEmpty(code))
                    carriers[code] = new CarrierRecord(code, name);
            }

            _logger.LogInformation("Loaded {Count} shipping lines from Softship", carriers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load carriers from Softship");
        }

        return carriers;
    }

    /// <summary>
    /// Loads countries from the Softship Country table.
    /// Softship schema: Country(CountryCode, CountryName, IsSanctioned, CurrencyCode, IsActive)
    /// </summary>
    private async Task<Dictionary<string, CountryRecord>> LoadCountriesAsync()
    {
        var countries = new Dictionary<string, CountryRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT CountryCode, CountryName, IsSanctioned, CurrencyCode FROM Country WHERE IsActive = 1",
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

            _logger.LogInformation("Loaded {Count} countries from Softship", countries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load countries from Softship");
        }

        return countries;
    }

    #endregion

    #region Historical data loaders (Softship schema)

    /// <summary>
    /// Checks whether the Softship database has file routing history.
    /// Softship schema: FileRouting is the per-leg routing table for job files.
    /// </summary>
    private async Task<bool> CheckFileDataAsync()
    {
        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(
                "SELECT CASE WHEN EXISTS (SELECT 1 FROM FileRouting) THEN 1 ELSE 0 END", conn);
            var result = await cmd.ExecuteScalarAsync();
            var has = Convert.ToInt32(result) == 1;
            _logger.LogInformation("Softship file routing history: {Status}",
                has ? "AVAILABLE" : "EMPTY (using estimates)");
            return has;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check file routing data availability");
            return false;
        }
    }

    /// <summary>
    /// Computes carrier delay statistics from FileRouting.
    /// Softship schema: FileRouting(FileId, CarrierCode, TransportMode, ScheduledDate,
    ///   DepartureDate, ArrivalDate, LoadPort, DischargePort, VesselName, VoyageNumber)
    /// Delay = actual arrival vs scheduled transit.
    /// </summary>
    private async Task<Dictionary<string, CarrierStats>> LoadCarrierStatsAsync()
    {
        var result = new Dictionary<string, CarrierStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasFileData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    RTRIM(CarrierCode) AS Carrier,
                    TransportMode AS Mode,
                    COUNT(*) AS SampleCount,
                    AVG(CAST(DATEDIFF(day, ScheduledDate, ArrivalDate) AS float)
                      - CAST(DATEDIFF(day, ScheduledDate, DepartureDate) AS float)) AS MeanDelayDays,
                    STDEV(CAST(DATEDIFF(day, ScheduledDate, ArrivalDate) AS float)
                        - CAST(DATEDIFF(day, ScheduledDate, DepartureDate) AS float)) AS DelayStdDev,
                    CAST(SUM(CASE WHEN ArrivalDate <= DATEADD(day, 1, ScheduledDate) THEN 1 ELSE 0 END) AS float)
                        / COUNT(*) AS OnTimeRatio
                FROM FileRouting
                WHERE CarrierCode IS NOT NULL AND CarrierCode <> ''
                  AND ScheduledDate IS NOT NULL
                  AND DepartureDate IS NOT NULL
                  AND ArrivalDate IS NOT NULL
                  AND ArrivalDate > DepartureDate
                GROUP BY RTRIM(CarrierCode), TransportMode
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
                result[key] = new CarrierStats(count, Math.Max(0, meanDelay),
                    Math.Max(0.1, stdDev), Math.Clamp(onTime, 0, 1));
            }

            _logger.LogInformation("Computed historical carrier stats for {Count} carrier/mode combinations from Softship",
                result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute carrier stats from Softship");
        }

        return result;
    }

    /// <summary>
    /// Computes route transit statistics from FileRouting.
    /// Transit = DATEDIFF(day, DepartureDate, ArrivalDate) per LoadPort/DischargePort pair.
    /// </summary>
    private async Task<Dictionary<string, RouteStats>> LoadRouteStatsAsync()
    {
        var result = new Dictionary<string, RouteStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasFileData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    RTRIM(LoadPort) AS LoadPort,
                    RTRIM(DischargePort) AS DischargePort,
                    TransportMode AS Mode,
                    COUNT(*) AS SampleCount,
                    AVG(CAST(DATEDIFF(day, DepartureDate, ArrivalDate) AS float)) AS MeanTransitDays,
                    STDEV(CAST(DATEDIFF(day, DepartureDate, ArrivalDate) AS float)) AS TransitStdDev,
                    CAST(SUM(CASE WHEN DATEDIFF(day, ScheduledDate, ArrivalDate) >
                         DATEDIFF(day, ScheduledDate, DepartureDate) + 1 THEN 1 ELSE 0 END) AS float)
                        / COUNT(*) AS CongestionProb,
                    AVG(CASE WHEN DATEDIFF(day, ScheduledDate, ArrivalDate) >
                         DATEDIFF(day, ScheduledDate, DepartureDate) + 1
                         THEN CAST(DATEDIFF(day, ScheduledDate, ArrivalDate)
                            - DATEDIFF(day, ScheduledDate, DepartureDate) AS float) END) AS CongestionDelayMean
                FROM FileRouting
                WHERE LoadPort IS NOT NULL AND LoadPort <> ''
                  AND DischargePort IS NOT NULL AND DischargePort <> ''
                  AND DepartureDate IS NOT NULL
                  AND ArrivalDate IS NOT NULL
                  AND ArrivalDate > DepartureDate
                GROUP BY RTRIM(LoadPort), RTRIM(DischargePort), TransportMode
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

            _logger.LogInformation("Computed historical route stats for {Count} port-pair/mode combinations from Softship",
                result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute route stats from Softship");
        }

        return result;
    }

    /// <summary>
    /// Computes customs hold statistics from CustomsEntry.
    /// Softship schema: CustomsEntry(EntryId, FileId, PortOfArrival, SubmittedDate, ReleaseDate)
    /// Hold probability = entries where (ReleaseDate - SubmittedDate) > 1 day / total entries.
    /// </summary>
    private async Task<Dictionary<string, CustomsStats>> LoadCustomsStatsAsync()
    {
        var result = new Dictionary<string, CustomsStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasFileData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    LEFT(RTRIM(PortOfArrival), 2) AS DestCountry,
                    COUNT(*) AS TotalEntries,
                    CAST(SUM(CASE WHEN DATEDIFF(day, SubmittedDate, ReleaseDate) > 1
                         THEN 1 ELSE 0 END) AS float) / COUNT(*) AS HoldProbability,
                    AVG(CASE WHEN DATEDIFF(day, SubmittedDate, ReleaseDate) > 1
                         THEN CAST(DATEDIFF(day, SubmittedDate, ReleaseDate) AS float) END) AS MeanHoldDays,
                    STDEV(CASE WHEN DATEDIFF(day, SubmittedDate, ReleaseDate) > 1
                          THEN CAST(DATEDIFF(day, SubmittedDate, ReleaseDate) AS float) END) AS HoldStdDev
                FROM CustomsEntry
                WHERE SubmittedDate IS NOT NULL
                  AND ReleaseDate IS NOT NULL
                  AND ReleaseDate >= SubmittedDate
                  AND PortOfArrival IS NOT NULL AND PortOfArrival <> ''
                GROUP BY LEFT(RTRIM(PortOfArrival), 2)
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

            _logger.LogInformation("Computed historical customs stats for {Count} countries from Softship", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute customs stats from Softship");
        }

        return result;
    }

    /// <summary>
    /// Computes average cost per file from FileCharge, grouped by transport mode.
    /// Softship schema: FileCharge(ChargeId, FileId, ChargeCode, Amount, CurrencyCode)
    /// FileHeader(FileId, TransportMode, ...)
    /// </summary>
    private async Task<Dictionary<string, CostStats>> LoadCostStatsAsync()
    {
        var result = new Dictionary<string, CostStats>(StringComparer.OrdinalIgnoreCase);
        if (!await _hasFileData.Value) return result;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
                SELECT
                    RTRIM(fh.TransportMode) AS Mode,
                    COUNT(DISTINCT fc.FileId) AS FileCount,
                    AVG(fc.Amount) AS MeanCostPerCharge,
                    SUM(fc.Amount) / NULLIF(COUNT(DISTINCT fc.FileId), 0) AS MeanCostPerFile
                FROM FileCharge fc
                INNER JOIN FileHeader fh ON fc.FileId = fh.FileId
                WHERE fc.Amount > 0
                  AND fh.TransportMode IS NOT NULL AND fh.TransportMode <> ''
                GROUP BY RTRIM(fh.TransportMode)
                HAVING COUNT(DISTINCT fc.FileId) >= @minSamples", conn);
            cmd.Parameters.AddWithValue("@minSamples", MinSampleSize);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var mode = reader.GetString(0).Trim();
                var fileCount = reader.GetInt32(1);
                var meanPerFile = reader.IsDBNull(3) ? 0 : Convert.ToDouble(reader.GetValue(3));

                var modeNorm = NormalizeTransportMode(mode);
                result[modeNorm] = new CostStats(fileCount, meanPerFile);
            }

            _logger.LogInformation("Computed historical cost stats for {Count} transport modes from Softship", result.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to compute cost stats from Softship");
        }

        return result;
    }

    private async Task<double> GetHistoricalCostOrEstimate(string mode)
    {
        var costs = await _costStats.Value;
        var modeNorm = NormalizeTransportMode(mode);
        if (costs.TryGetValue(modeNorm, out var c))
            return c.MeanCostPerFile;
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
    /// Maps Softship transport mode codes to Foresight modes.
    /// Softship uses: SEA, AIR, ROA, RAI (similar to CW1 but also Ocean, Road, Rail directly).
    /// </summary>
    private static string NormalizeTransportMode(string mode) => mode.Trim().ToUpperInvariant() switch
    {
        "SEA" or "FCL" or "LCL" or "OCEAN" => "Ocean",
        "AIR" => "Air",
        "ROA" or "ROD" or "ROAD" => "Road",
        "RAI" or "RAL" or "RAIL" => "Rail",
        _ => mode.Trim()
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
    internal sealed record CostStats(int FileCount, double MeanCostPerFile);
}

public sealed record SoftshipStatus(bool Connected, int PortCount, int CarrierCount, int CountryCount,
    bool HasFileHistory, int CarrierStatCount, int RouteStatCount, int CustomsStatCount);
