using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Data.Mock;

public sealed class MockDataAdapter : IDataAdapter
{
    private static readonly Dictionary<string, CarrierPrior> Carriers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MAERSK"] = new CarrierPrior
        {
            CarrierCode = "MAERSK", Mode = "Ocean",
            ReliabilityScore = 0.88, MeanDelayDays = 1.2, DelayStdDev = 1.8, BaseCostPerUnit = 2200
        },
        ["MSC"] = new CarrierPrior
        {
            CarrierCode = "MSC", Mode = "Ocean",
            ReliabilityScore = 0.85, MeanDelayDays = 1.5, DelayStdDev = 2.0, BaseCostPerUnit = 2000
        },
        ["COSCO"] = new CarrierPrior
        {
            CarrierCode = "COSCO", Mode = "Ocean",
            ReliabilityScore = 0.82, MeanDelayDays = 1.8, DelayStdDev = 2.5, BaseCostPerUnit = 1800
        },
        ["HAPAG"] = new CarrierPrior
        {
            CarrierCode = "HAPAG", Mode = "Ocean",
            ReliabilityScore = 0.87, MeanDelayDays = 1.3, DelayStdDev = 1.9, BaseCostPerUnit = 2100
        },
        ["FEDEX_AIR"] = new CarrierPrior
        {
            CarrierCode = "FEDEX_AIR", Mode = "Air",
            ReliabilityScore = 0.92, MeanDelayDays = 0.3, DelayStdDev = 0.5, BaseCostPerUnit = 5500
        },
        ["DHL_AIR"] = new CarrierPrior
        {
            CarrierCode = "DHL_AIR", Mode = "Air",
            ReliabilityScore = 0.91, MeanDelayDays = 0.4, DelayStdDev = 0.6, BaseCostPerUnit = 5200
        },
        ["DB_SCHENKER"] = new CarrierPrior
        {
            CarrierCode = "DB_SCHENKER", Mode = "Road",
            ReliabilityScore = 0.90, MeanDelayDays = 0.5, DelayStdDev = 1.0, BaseCostPerUnit = 1500
        },
        ["UNKNOWN"] = new CarrierPrior
        {
            CarrierCode = "UNKNOWN", Mode = "Ocean",
            ReliabilityScore = 0.80, MeanDelayDays = 2.0, DelayStdDev = 3.0, BaseCostPerUnit = 2000
        }
    };

    private static readonly List<RoutePrior> Routes =
    [
        new RoutePrior
        {
            Origin = "CNSHA", Destination = "USLAX", Mode = "Ocean",
            BaseTransitDays = 14, TransitStdDev = 2.5,
            PortCongestionProbability = 0.25, PortCongestionDelayMean = 3, PortCongestionDelayStdDev = 2
        },
        new RoutePrior
        {
            Origin = "CNSHA", Destination = "NLRTM", Mode = "Ocean",
            BaseTransitDays = 28, TransitStdDev = 4.0,
            PortCongestionProbability = 0.15, PortCongestionDelayMean = 2, PortCongestionDelayStdDev = 1.5
        },
        new RoutePrior
        {
            Origin = "CNSHA", Destination = "AUSYD", Mode = "Ocean",
            BaseTransitDays = 18, TransitStdDev = 3.0,
            PortCongestionProbability = 0.10, PortCongestionDelayMean = 2, PortCongestionDelayStdDev = 1
        },
        new RoutePrior
        {
            Origin = "DEHAM", Destination = "USNYC", Mode = "Ocean",
            BaseTransitDays = 12, TransitStdDev = 2.0,
            PortCongestionProbability = 0.20, PortCongestionDelayMean = 2.5, PortCongestionDelayStdDev = 1.5
        },
        new RoutePrior
        {
            Origin = "CNSHA", Destination = "USLAX", Mode = "Air",
            BaseTransitDays = 2, TransitStdDev = 0.5,
            PortCongestionProbability = 0.05, PortCongestionDelayMean = 0.5, PortCongestionDelayStdDev = 0.3
        },
        new RoutePrior
        {
            Origin = "DEHAM", Destination = "AUSYD", Mode = "Ocean",
            BaseTransitDays = 32, TransitStdDev = 5.0,
            PortCongestionProbability = 0.12, PortCongestionDelayMean = 2, PortCongestionDelayStdDev = 1.5
        }
    ];

    private static readonly Dictionary<string, CustomsPrior> CustomsByCountry = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = new CustomsPrior
        {
            Country = "US", BaseHoldProbability = 0.06, HazmatHoldMultiplier = 3.5,
            HoldDelayMeanDays = 4, HoldDelayStdDev = 2.5,
            HighRiskCommodities = ["electronics", "chemicals", "pharmaceuticals", "lithium_batteries"],
            HighRiskCommodityMultiplier = 2.0
        },
        ["CN"] = new CustomsPrior
        {
            Country = "CN", BaseHoldProbability = 0.08, HazmatHoldMultiplier = 4.0,
            HoldDelayMeanDays = 6, HoldDelayStdDev = 3,
            HighRiskCommodities = ["food", "chemicals", "cosmetics"],
            HighRiskCommodityMultiplier = 2.5
        },
        ["AU"] = new CustomsPrior
        {
            Country = "AU", BaseHoldProbability = 0.10, HazmatHoldMultiplier = 3.0,
            HoldDelayMeanDays = 5, HoldDelayStdDev = 3,
            HighRiskCommodities = ["food", "agriculture", "wood", "biologicals"],
            HighRiskCommodityMultiplier = 3.0
        },
        ["NL"] = new CustomsPrior
        {
            Country = "NL", BaseHoldProbability = 0.04, HazmatHoldMultiplier = 2.5,
            HoldDelayMeanDays = 3, HoldDelayStdDev = 1.5,
            HighRiskCommodities = ["chemicals"],
            HighRiskCommodityMultiplier = 2.0
        },
        ["DE"] = new CustomsPrior
        {
            Country = "DE", BaseHoldProbability = 0.04, HazmatHoldMultiplier = 2.5,
            HoldDelayMeanDays = 3, HoldDelayStdDev = 1.5,
            HighRiskCommodities = ["chemicals", "pharmaceuticals"],
            HighRiskCommodityMultiplier = 2.0
        }
    };

    private static readonly Dictionary<string, DemurragePrior> Demurrage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ocean"] = new DemurragePrior { Mode = "Ocean", FreeTimeDays = 7, DailyRate = 150 },
        ["Air"] = new DemurragePrior { Mode = "Air", FreeTimeDays = 3, DailyRate = 200 },
        ["Road"] = new DemurragePrior { Mode = "Road", FreeTimeDays = 2, DailyRate = 100 },
        ["Rail"] = new DemurragePrior { Mode = "Rail", FreeTimeDays = 5, DailyRate = 120 }
    };

    private static readonly List<RatePrior> RatePriors =
    [
        new RatePrior
        {
            Origin = "CNSHA", Destination = "USLAX", Mode = "Ocean",
            MarketBenchmarkRate = 2600, RateVolatilityPercent = 0.15,
            SeasonalAdjustment = 1.1, FuelSurchargePercent = 0.08
        },
        new RatePrior
        {
            Origin = "CNSHA", Destination = "NLRTM", Mode = "Ocean",
            MarketBenchmarkRate = 3200, RateVolatilityPercent = 0.12,
            SeasonalAdjustment = 1.0, FuelSurchargePercent = 0.09
        },
        new RatePrior
        {
            Origin = "CNSHA", Destination = "AUSYD", Mode = "Ocean",
            MarketBenchmarkRate = 2100, RateVolatilityPercent = 0.10,
            SeasonalAdjustment = 0.95, FuelSurchargePercent = 0.07
        },
        new RatePrior
        {
            Origin = "DEHAM", Destination = "USNYC", Mode = "Ocean",
            MarketBenchmarkRate = 2400, RateVolatilityPercent = 0.14,
            SeasonalAdjustment = 1.05, FuelSurchargePercent = 0.08
        },
        new RatePrior
        {
            Origin = "CNSHA", Destination = "USLAX", Mode = "Air",
            MarketBenchmarkRate = 5800, RateVolatilityPercent = 0.18,
            SeasonalAdjustment = 1.15, FuelSurchargePercent = 0.12
        }
    ];

    private static readonly List<QuotationPrior> QuotationPriors =
    [
        new QuotationPrior
        {
            Origin = "CNSHA", Destination = "USLAX", Mode = "Ocean",
            BaseWinProbability = 0.45, MarginSensitivity = 2.5,
            AverageCompetitorDiscount = 0.05, HistoricalConversionRate = 0.35
        },
        new QuotationPrior
        {
            Origin = "CNSHA", Destination = "NLRTM", Mode = "Ocean",
            BaseWinProbability = 0.40, MarginSensitivity = 3.0,
            AverageCompetitorDiscount = 0.07, HistoricalConversionRate = 0.30
        },
        new QuotationPrior
        {
            Origin = "CNSHA", Destination = "AUSYD", Mode = "Ocean",
            BaseWinProbability = 0.50, MarginSensitivity = 2.0,
            AverageCompetitorDiscount = 0.04, HistoricalConversionRate = 0.40
        },
        new QuotationPrior
        {
            Origin = "DEHAM", Destination = "USNYC", Mode = "Ocean",
            BaseWinProbability = 0.42, MarginSensitivity = 2.8,
            AverageCompetitorDiscount = 0.06, HistoricalConversionRate = 0.33
        },
        new QuotationPrior
        {
            Origin = "CNSHA", Destination = "USLAX", Mode = "Air",
            BaseWinProbability = 0.35, MarginSensitivity = 3.5,
            AverageCompetitorDiscount = 0.08, HistoricalConversionRate = 0.25
        }
    ];

    public Task<CarrierPrior?> GetCarrierPriorAsync(string carrierCode, string mode, CancellationToken ct = default)
    {
        Carriers.TryGetValue(carrierCode, out var prior);
        return Task.FromResult(prior);
    }

    public Task<RoutePrior?> GetRoutePriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        var prior = Routes.FirstOrDefault(r =>
            r.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase) &&
            r.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
            r.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(prior);
    }

    public Task<CustomsPrior?> GetCustomsPriorAsync(string country, CancellationToken ct = default)
    {
        CustomsByCountry.TryGetValue(country, out var prior);
        return Task.FromResult(prior);
    }

    public Task<DemurragePrior?> GetDemurragePriorAsync(string mode, CancellationToken ct = default)
    {
        Demurrage.TryGetValue(mode, out var prior);
        return Task.FromResult(prior);
    }

    public Task<RatePrior?> GetRatePriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        var prior = RatePriors.FirstOrDefault(r =>
            r.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase) &&
            r.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
            r.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(prior);
    }

    public Task<QuotationPrior?> GetQuotationPriorAsync(string origin, string destination, string mode, CancellationToken ct = default)
    {
        var prior = QuotationPriors.FirstOrDefault(q =>
            q.Origin.Equals(origin, StringComparison.OrdinalIgnoreCase) &&
            q.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
            q.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(prior);
    }

    private static readonly List<PortInfo> MockPorts =
    [
        new("CNSHA", "Shanghai", "CN"), new("USLAX", "Los Angeles", "US"),
        new("USNYC", "New York", "US"), new("DEHAM", "Hamburg", "DE"),
        new("NLRTM", "Rotterdam", "NL"), new("AUSYD", "Sydney", "AU"),
        new("SGSIN", "Singapore", "SG"), new("HKHKG", "Hong Kong", "HK"),
        new("GBFXT", "Felixstowe", "GB"), new("JPTYO", "Tokyo", "JP")
    ];

    public Task<IReadOnlyList<PortInfo>> SearchPortsAsync(string query, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return Task.FromResult<IReadOnlyList<PortInfo>>(Array.Empty<PortInfo>());

        var results = MockPorts
            .Where(p => p.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();
        return Task.FromResult<IReadOnlyList<PortInfo>>(results);
    }

    public Task<IReadOnlyList<CarrierInfo>> GetCarrierListAsync(CancellationToken ct = default)
    {
        var list = Carriers.Values
            .Select(c => new CarrierInfo(c.CarrierCode, c.CarrierCode))
            .OrderBy(c => c.Code)
            .ToList();
        return Task.FromResult<IReadOnlyList<CarrierInfo>>(list);
    }
}
