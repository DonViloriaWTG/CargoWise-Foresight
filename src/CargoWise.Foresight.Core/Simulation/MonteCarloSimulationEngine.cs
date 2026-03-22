using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CargoWise.Foresight.Core.Interfaces;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Core.Simulation;

public sealed class MonteCarloSimulationEngine : ISimulationEngine
{
    private readonly IDataAdapter _dataAdapter;
    private readonly ILogger<MonteCarloSimulationEngine> _logger;

    public MonteCarloSimulationEngine(IDataAdapter dataAdapter, ILogger<MonteCarloSimulationEngine> logger)
    {
        _dataAdapter = dataAdapter;
        _logger = logger;
    }

    public async Task<SimulationResult> RunAsync(SimulationRequest request, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var traces = new List<string>();

        _logger.LogInformation("Starting simulation {RequestId} with seed={Seed}, runs={Runs}",
            request.RequestId, request.Seed, request.SimulationRuns);

        var shipment = request.Baseline.Shipment;

        // Fetch priors
        var carrierCode = ResolveCarrier(shipment, request.ChangeSet);
        var origin = ResolveOrigin(shipment, request.ChangeSet);
        var destination = ResolveDestination(shipment, request.ChangeSet);
        var mode = shipment.Mode;

        var carrierPrior = await _dataAdapter.GetCarrierPriorAsync(carrierCode, mode, ct);
        var routePrior = await _dataAdapter.GetRoutePriorAsync(origin, destination, mode, ct);
        var demurragePrior = await _dataAdapter.GetDemurragePriorAsync(mode, ct);

        // Customs priors for all destination countries
        var countriesInvolved = request.Baseline.Compliance?.CountriesInvolved ?? [destination];
        var customsPriors = new List<CustomsPrior>();
        foreach (var country in countriesInvolved)
        {
            var cp = await _dataAdapter.GetCustomsPriorAsync(country, ct);
            if (cp != null) customsPriors.Add(cp);
        }

        // Defaults
        carrierPrior ??= new CarrierPrior { CarrierCode = carrierCode, Mode = mode };
        routePrior ??= new RoutePrior { Origin = origin, Destination = destination, Mode = mode };
        demurragePrior ??= new DemurragePrior { Mode = mode };

        traces.Add($"Priors loaded: carrier={carrierPrior.CarrierCode}, route={origin}->{destination}, customs countries={countriesInvolved.Count}");

        // Apply departure shift
        double departureShiftDays = ResolveDepartureShift(request.ChangeSet);

        // Run Monte Carlo
        var rng = new Random(request.Seed);
        int n = Math.Clamp(request.SimulationRuns, 1, 100_000);

        var etaSamples = new double[n];
        var costSamples = new double[n];
        var slaBreachSamples = new double[n];

        double baseCost = ComputeBaseCost(request, carrierPrior);

        for (int i = 0; i < n; i++)
        {
            // Transit time
            double transitDays = SampleNormal(rng, routePrior.BaseTransitDays, routePrior.TransitStdDev);
            transitDays = Math.Max(1.0, transitDays);

            // Port congestion
            if (rng.NextDouble() < routePrior.PortCongestionProbability)
            {
                double congestionDelay = SampleNormal(rng, routePrior.PortCongestionDelayMean, routePrior.PortCongestionDelayStdDev);
                transitDays += Math.Max(0, congestionDelay);
            }

            // Carrier delay
            double carrierDelay = SampleNormal(rng, carrierPrior.MeanDelayDays, carrierPrior.DelayStdDev);
            transitDays += Math.Max(0, carrierDelay);

            // Customs hold
            foreach (var cp in customsPriors)
            {
                double holdProb = cp.BaseHoldProbability;
                if (shipment.Hazmat) holdProb *= cp.HazmatHoldMultiplier;

                var commodities = request.Baseline.Compliance?.Commodities ?? [];
                bool hasHighRisk = commodities.Any(c => cp.HighRiskCommodities.Contains(c, StringComparer.OrdinalIgnoreCase));
                if (hasHighRisk) holdProb *= cp.HighRiskCommodityMultiplier;

                holdProb = Math.Min(holdProb, 1.0);

                if (rng.NextDouble() < holdProb)
                {
                    double holdDelay = SampleNormal(rng, cp.HoldDelayMeanDays, cp.HoldDelayStdDev);
                    transitDays += Math.Max(0, holdDelay);
                }
            }

            // Apply departure shift
            double totalDays = transitDays + departureShiftDays;
            etaSamples[i] = totalDays;

            // Cost: base + demurrage if late beyond free time
            double cost = baseCost;
            double overFreeTime = totalDays - demurragePrior.FreeTimeDays;
            if (overFreeTime > 0)
            {
                cost += overFreeTime * demurragePrior.DailyRate;
            }
            costSamples[i] = cost;

            // SLA breach
            double slaDays = GetPrimarySlaTarget(request);
            slaBreachSamples[i] = totalDays > slaDays ? 1.0 : 0.0;
        }

        sw.Stop();

        var etaDist = BuildDistribution(etaSamples);
        var costDist = BuildDistribution(costSamples);
        var slaDist = BuildDistribution(slaBreachSamples);

        double slaBreachProb = slaBreachSamples.Average();
        double complianceRisk = ComputeComplianceRisk(request, customsPriors);

        var risks = BuildRisks(slaBreachProb, complianceRisk, request, routePrior);
        var recommendations = BuildRecommendations(slaBreachProb, costDist.P50, request);

        double overallRisk = Math.Min(1.0, (slaBreachProb * 0.5) + (complianceRisk * 0.3) + ((1.0 - carrierPrior.ReliabilityScore) * 0.2));

        string outcome = overallRisk switch
        {
            < 0.2 => "Low risk. Proposed change appears safe.",
            < 0.5 => "Moderate risk. Review flagged items before proceeding.",
            < 0.8 => "High risk. Significant probability of adverse outcomes.",
            _ => "Critical risk. Strongly recommend reconsidering this change."
        };

        traces.Add($"Simulation completed in {sw.Elapsed.TotalMilliseconds:F1}ms");

        _logger.LogInformation("Simulation {RequestId} completed in {Duration}ms, overallRisk={Risk:F3}",
            request.RequestId, sw.Elapsed.TotalMilliseconds, overallRisk);

        return new SimulationResult
        {
            RequestId = request.RequestId,
            Summary = new SimulationSummary
            {
                Outcome = outcome,
                OverallRiskScore = Math.Round(overallRisk, 4),
                SimulationRuns = n,
                Seed = request.Seed,
                DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 2)
            },
            Distributions = new DistributionSet
            {
                EtaDays = etaDist,
                CostUsd = costDist,
                SlaBreachProbability = slaDist
            },
            Risks = risks,
            Recommendations = recommendations,
            Traces = new SimulationTraces
            {
                Steps = traces,
                InternalState = new Dictionary<string, object>
                {
                    ["carrierCode"] = carrierCode,
                    ["origin"] = origin,
                    ["destination"] = destination,
                    ["departureShiftDays"] = departureShiftDays
                }
            }
        };
    }

    private static string ResolveCarrier(ShipmentInfo shipment, ChangeSet change)
    {
        if (change.ChangeType == ChangeType.CarrierSwap &&
            change.Parameters.TryGetValue("newCarrier", out var nc))
            return nc?.ToString() ?? shipment.Carrier ?? "UNKNOWN";
        return shipment.Carrier ?? "UNKNOWN";
    }

    private static string ResolveOrigin(ShipmentInfo shipment, ChangeSet change)
    {
        if (change.ChangeType == ChangeType.RouteChange &&
            change.Parameters.TryGetValue("newOrigin", out var no))
            return no?.ToString() ?? shipment.Origin;
        return shipment.Origin;
    }

    private static string ResolveDestination(ShipmentInfo shipment, ChangeSet change)
    {
        if (change.ChangeType == ChangeType.RouteChange &&
            change.Parameters.TryGetValue("newDestination", out var nd))
            return nd?.ToString() ?? shipment.Destination;
        return shipment.Destination;
    }

    private static double ResolveDepartureShift(ChangeSet change)
    {
        if (change.ChangeType == ChangeType.DepartureShift &&
            change.Parameters.TryGetValue("shiftDays", out var sd))
        {
            if (sd is double d) return d;
            if (double.TryParse(sd?.ToString(), out var parsed)) return parsed;
        }
        return 0.0;
    }

    private static double ComputeBaseCost(SimulationRequest request, CarrierPrior carrier)
    {
        var finance = request.Baseline.Finance;
        if (finance != null && finance.RateLineItems.Count > 0)
            return (double)finance.RateLineItems.Sum(r => r.Amount);

        return carrier.BaseCostPerUnit;
    }

    private static double GetPrimarySlaTarget(SimulationRequest request)
    {
        var sla = request.Baseline.Workflow?.SlaTargets.FirstOrDefault();
        if (sla != null) return sla.TargetDays;

        // Default: use horizon or 21 days
        return request.HorizonDays > 0 ? request.HorizonDays : 21.0;
    }

    private static double ComputeComplianceRisk(SimulationRequest request, List<CustomsPrior> priors)
    {
        if (priors.Count == 0) return 0.05;

        double risk = 0.0;
        foreach (var cp in priors)
        {
            double r = cp.BaseHoldProbability;
            if (request.Baseline.Shipment.Hazmat) r *= cp.HazmatHoldMultiplier;

            var commodities = request.Baseline.Compliance?.Commodities ?? [];
            if (commodities.Any(c => cp.HighRiskCommodities.Contains(c, StringComparer.OrdinalIgnoreCase)))
                r *= cp.HighRiskCommodityMultiplier;

            risk = Math.Max(risk, Math.Min(r, 1.0));
        }
        return risk;
    }

    private static List<RiskFlag> BuildRisks(double slaBreachProb, double complianceRisk, SimulationRequest request, RoutePrior route)
    {
        var risks = new List<RiskFlag>();

        if (slaBreachProb > 0.05)
        {
            risks.Add(new RiskFlag
            {
                Type = "SLA_BREACH",
                Probability = Math.Round(slaBreachProb, 4),
                Severity = slaBreachProb switch { > 0.5 => "Critical", > 0.3 => "High", > 0.15 => "Medium", _ => "Low" },
                RationaleFacts = $"SLA breach probability: {slaBreachProb:P1}. Based on {request.SimulationRuns} simulation runs with carrier/route historical data.",
                Mitigations = ["Consider expedited shipping option", "Negotiate extended SLA window", "Pre-alert destination for priority handling"]
            });
        }

        if (complianceRisk > 0.1)
        {
            risks.Add(new RiskFlag
            {
                Type = "CUSTOMS_HOLD",
                Probability = Math.Round(complianceRisk, 4),
                Severity = complianceRisk switch { > 0.4 => "High", > 0.2 => "Medium", _ => "Low" },
                RationaleFacts = $"Customs hold risk: {complianceRisk:P1}. Factors: hazmat={request.Baseline.Shipment.Hazmat}, countries={string.Join(",", request.Baseline.Compliance?.CountriesInvolved ?? [])}.",
                Mitigations = ["Pre-file customs documentation", "Engage licensed customs broker", "Obtain pre-clearance certificates"]
            });
        }

        if (route.PortCongestionProbability > 0.2)
        {
            risks.Add(new RiskFlag
            {
                Type = "PORT_CONGESTION",
                Probability = Math.Round(route.PortCongestionProbability, 4),
                Severity = route.PortCongestionProbability > 0.4 ? "High" : "Medium",
                RationaleFacts = $"Port congestion probability: {route.PortCongestionProbability:P0}. Mean delay when congested: {route.PortCongestionDelayMean:F1} days.",
                Mitigations = ["Route via alternative port", "Schedule outside peak season", "Use rail/road alternatives for last mile"]
            });
        }

        return risks;
    }

    private static List<Recommendation> BuildRecommendations(double slaBreachProb, double costP50, SimulationRequest request)
    {
        var recs = new List<Recommendation>();

        if (slaBreachProb > 0.15)
        {
            recs.Add(new Recommendation
            {
                Option = "ExpressMode",
                Description = "Switch to express/air freight to reduce transit time and SLA breach risk.",
                ExpectedDeltas = new() { ["etaDays"] = -5.0, ["costUsd"] = costP50 * 0.4 },
                Confidence = 0.75
            });
        }

        if (request.ChangeSet.ChangeType == ChangeType.CarrierSwap)
        {
            recs.Add(new Recommendation
            {
                Option = "KeepCurrentCarrier",
                Description = "Retain current carrier if its historical reliability is higher.",
                ExpectedDeltas = [],
                Confidence = 0.6
            });
        }

        recs.Add(new Recommendation
        {
            Option = "SplitShipment",
            Description = "Consider splitting into multiple smaller shipments to reduce per-shipment risk exposure.",
            ExpectedDeltas = new() { ["costUsd"] = costP50 * 0.15 },
            Confidence = 0.5
        });

        return recs;
    }

    internal static Distribution BuildDistribution(double[] samples)
    {
        Array.Sort(samples);
        int n = samples.Length;

        double mean = samples.Average();
        double variance = samples.Sum(s => (s - mean) * (s - mean)) / n;
        double stdDev = Math.Sqrt(variance);

        double p50 = Percentile(samples, 0.50);
        double p80 = Percentile(samples, 0.80);
        double p95 = Percentile(samples, 0.95);

        var histogram = BuildHistogram(samples, 10);

        return new Distribution
        {
            P50 = Math.Round(p50, 2),
            P80 = Math.Round(p80, 2),
            P95 = Math.Round(p95, 2),
            Mean = Math.Round(mean, 2),
            StdDev = Math.Round(stdDev, 2),
            Histogram = histogram
        };
    }

    private static double Percentile(double[] sortedSamples, double p)
    {
        double index = p * (sortedSamples.Length - 1);
        int lower = (int)Math.Floor(index);
        int upper = Math.Min(lower + 1, sortedSamples.Length - 1);
        double frac = index - lower;
        return sortedSamples[lower] * (1.0 - frac) + sortedSamples[upper] * frac;
    }

    private static List<HistogramBucket> BuildHistogram(double[] sortedSamples, int bucketCount)
    {
        if (sortedSamples.Length == 0) return [];

        double min = sortedSamples[0];
        double max = sortedSamples[^1];

        if (Math.Abs(max - min) < 0.001)
        {
            return [new HistogramBucket { LowerBound = Math.Round(min, 2), UpperBound = Math.Round(max, 2), Count = sortedSamples.Length }];
        }

        double bucketWidth = (max - min) / bucketCount;
        var buckets = new List<HistogramBucket>();

        for (int b = 0; b < bucketCount; b++)
        {
            double lo = min + b * bucketWidth;
            double hi = lo + bucketWidth;
            int count = sortedSamples.Count(s => s >= lo && (b == bucketCount - 1 ? s <= hi : s < hi));
            buckets.Add(new HistogramBucket
            {
                LowerBound = Math.Round(lo, 2),
                UpperBound = Math.Round(hi, 2),
                Count = count
            });
        }

        return buckets;
    }

    internal static double SampleNormal(Random rng, double mean, double stdDev)
    {
        // Box-Muller transform
        double u1 = 1.0 - rng.NextDouble();
        double u2 = 1.0 - rng.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + stdDev * z;
    }
}
