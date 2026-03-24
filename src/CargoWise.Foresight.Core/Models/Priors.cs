namespace CargoWise.Foresight.Core.Models;

public sealed record CarrierPrior
{
    public required string CarrierCode { get; init; }
    public required string Mode { get; init; }
    public double ReliabilityScore { get; init; } = 0.85;
    public double MeanDelayDays { get; init; } = 1.5;
    public double DelayStdDev { get; init; } = 2.0;
    public double BaseCostPerUnit { get; init; } = 1000.0;
}

public sealed record RoutePrior
{
    public required string Origin { get; init; }
    public required string Destination { get; init; }
    public required string Mode { get; init; }
    public double BaseTransitDays { get; init; } = 14.0;
    public double TransitStdDev { get; init; } = 3.0;
    public double PortCongestionProbability { get; init; } = 0.15;
    public double PortCongestionDelayMean { get; init; } = 3.0;
    public double PortCongestionDelayStdDev { get; init; } = 2.0;
}

public sealed record CustomsPrior
{
    public required string Country { get; init; }
    public double BaseHoldProbability { get; init; } = 0.05;
    public double HazmatHoldMultiplier { get; init; } = 3.0;
    public double HoldDelayMeanDays { get; init; } = 5.0;
    public double HoldDelayStdDev { get; init; } = 3.0;
    public List<string> HighRiskCommodities { get; init; } = [];
    public double HighRiskCommodityMultiplier { get; init; } = 2.5;
}

public sealed record DemurragePrior
{
    public required string Mode { get; init; }
    public double FreeTimeDays { get; init; } = 7.0;
    public double DailyRate { get; init; } = 150.0;
}

public sealed record RatePrior
{
    public required string Origin { get; init; }
    public required string Destination { get; init; }
    public required string Mode { get; init; }
    public double MarketBenchmarkRate { get; init; } = 2500.0;
    public double RateVolatilityPercent { get; init; } = 0.12;
    public double SeasonalAdjustment { get; init; } = 1.0;
    public double FuelSurchargePercent { get; init; } = 0.08;
}

public sealed record QuotationPrior
{
    public required string Origin { get; init; }
    public required string Destination { get; init; }
    public required string Mode { get; init; }
    public double BaseWinProbability { get; init; } = 0.45;
    public double MarginSensitivity { get; init; } = 2.5;
    public double AverageCompetitorDiscount { get; init; } = 0.05;
    public double HistoricalConversionRate { get; init; } = 0.35;
}
