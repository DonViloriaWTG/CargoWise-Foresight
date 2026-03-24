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
        return request.ChangeSet.ChangeType switch
        {
            ChangeType.RateChange => await RunRateChangeAsync(request, ct),
            ChangeType.QuotationChange => await RunQuotationChangeAsync(request, ct),
            _ => await RunTransitSimulationAsync(request, ct)
        };
    }

    private async Task<SimulationResult> RunTransitSimulationAsync(SimulationRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var traces = new List<string>();

        _logger.LogInformation("Starting simulation {RequestId} with seed={Seed}, runs={Runs}",
            request.RequestId, request.Seed, request.SimulationRuns);

        var shipment = request.Baseline.Shipment;

        // Fetch priors for the CHANGED scenario
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

        // Check mode feasibility for the route
        var modeFeasibility = CheckModeFeasibility(mode, origin, destination);
        if (modeFeasibility != null)
            traces.Add($"WARNING: {modeFeasibility.RationaleFacts}");

        double departureShiftDays = ResolveDepartureShift(request.ChangeSet);
        int n = Math.Clamp(request.SimulationRuns, 1, 100_000);
        double baseCost = ComputeBaseCost(request, carrierPrior);
        double slaDays = GetPrimarySlaTarget(request);

        // Run Monte Carlo for the CHANGED scenario
        var changed = RunTransitLoop(new Random(request.Seed), n, carrierPrior, routePrior,
            demurragePrior, customsPriors, request, departureShiftDays, baseCost, slaDays);

        // Run Monte Carlo for the BASELINE (original, unchanged) scenario
        var baselineCarrierCode = shipment.Carrier ?? "UNKNOWN";
        var baselineCarrierPrior = await _dataAdapter.GetCarrierPriorAsync(baselineCarrierCode, mode, ct)
            ?? new CarrierPrior { CarrierCode = baselineCarrierCode, Mode = mode };
        var baselineRoutePrior = await _dataAdapter.GetRoutePriorAsync(shipment.Origin, shipment.Destination, mode, ct)
            ?? new RoutePrior { Origin = shipment.Origin, Destination = shipment.Destination, Mode = mode };
        double baselineBaseCost = ComputeBaseCost(request, baselineCarrierPrior);

        var baseline = RunTransitLoop(new Random(request.Seed), n, baselineCarrierPrior, baselineRoutePrior,
            demurragePrior, customsPriors, request, 0.0, baselineBaseCost, slaDays);

        sw.Stop();

        var etaDist = BuildDistribution(changed.Eta);
        var costDist = BuildDistribution(changed.Cost);
        var slaDist = BuildDistribution(changed.SlaBreach);

        var baselineEtaDist = BuildDistribution(baseline.Eta);
        var baselineCostDist = BuildDistribution(baseline.Cost);
        var baselineSlaDist = BuildDistribution(baseline.SlaBreach);

        double slaBreachProb = changed.SlaBreach.Average();
        double complianceRisk = ComputeComplianceRisk(request, customsPriors);

        var risks = BuildRisks(slaBreachProb, complianceRisk, request, routePrior);
        if (modeFeasibility != null) risks.Insert(0, modeFeasibility);
        var recommendations = BuildRecommendations(slaBreachProb, costDist.P50, request);

        double modePenalty = modeFeasibility?.Type switch
        {
            "MODE_INFEASIBLE" => 0.5,
            "MODE_IMPRACTICAL" => 0.2,
            _ => 0.0
        };
        double overallRisk = Math.Min(1.0, (slaBreachProb * 0.5) + (complianceRisk * 0.3) + ((1.0 - carrierPrior.ReliabilityScore) * 0.2) + modePenalty);

        string outcome = overallRisk switch
        {
            < 0.2 => "Low risk. Proposed change appears safe.",
            < 0.5 => "Moderate risk. Review flagged items before proceeding.",
            < 0.8 => "High risk. Significant probability of adverse outcomes.",
            _ => "Critical risk. Strongly recommend reconsidering this change."
        };

        traces.Add($"Baseline: carrier={baselineCarrierCode}, route={shipment.Origin}->{shipment.Destination}");
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
            },
            Baseline = new BaselineComparison
            {
                Distributions = new DistributionSet
                {
                    EtaDays = baselineEtaDist,
                    CostUsd = baselineCostDist,
                    SlaBreachProbability = baselineSlaDist
                },
                Deltas = new Dictionary<string, double>
                {
                    ["etaDaysP50"] = Math.Round(etaDist.P50 - baselineEtaDist.P50, 2),
                    ["etaDaysP95"] = Math.Round(etaDist.P95 - baselineEtaDist.P95, 2),
                    ["costUsdP50"] = Math.Round(costDist.P50 - baselineCostDist.P50, 2),
                    ["costUsdP95"] = Math.Round(costDist.P95 - baselineCostDist.P95, 2),
                    ["slaBreachProb"] = Math.Round(slaBreachProb - baseline.SlaBreach.Average(), 4)
                }
            }
        };
    }

    private record TransitSamples(double[] Eta, double[] Cost, double[] SlaBreach);

    private static TransitSamples RunTransitLoop(Random rng, int n,
        CarrierPrior carrierPrior, RoutePrior routePrior, DemurragePrior demurragePrior,
        List<CustomsPrior> customsPriors, SimulationRequest request,
        double departureShiftDays, double baseCost, double slaDays)
    {
        var etaSamples = new double[n];
        var costSamples = new double[n];
        var slaBreachSamples = new double[n];
        var shipment = request.Baseline.Shipment;

        for (int i = 0; i < n; i++)
        {
            double transitDays = SampleNormal(rng, routePrior.BaseTransitDays, routePrior.TransitStdDev);
            transitDays = Math.Max(1.0, transitDays);

            if (rng.NextDouble() < routePrior.PortCongestionProbability)
            {
                double congestionDelay = SampleNormal(rng, routePrior.PortCongestionDelayMean, routePrior.PortCongestionDelayStdDev);
                transitDays += Math.Max(0, congestionDelay);
            }

            double carrierDelay = SampleNormal(rng, carrierPrior.MeanDelayDays, carrierPrior.DelayStdDev);
            transitDays += Math.Max(0, carrierDelay);

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

            double totalDays = transitDays + departureShiftDays;
            etaSamples[i] = totalDays;

            double cost = baseCost;
            double overFreeTime = totalDays - demurragePrior.FreeTimeDays;
            if (overFreeTime > 0)
            {
                cost += overFreeTime * demurragePrior.DailyRate;
            }
            costSamples[i] = cost;

            slaBreachSamples[i] = totalDays > slaDays ? 1.0 : 0.0;
        }

        return new TransitSamples(etaSamples, costSamples, slaBreachSamples);
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

    private static RiskFlag? CheckModeFeasibility(string mode, string origin, string destination)
    {
        var upperMode = mode.ToUpperInvariant();
        if (upperMode is not ("ROAD" or "RAIL")) return null;

        // Extract country codes (first 2 chars of UN/LOCODE)
        var originCountry = origin.Length >= 2 ? origin[..2].ToUpperInvariant() : "";
        var destCountry = destination.Length >= 2 ? destination[..2].ToUpperInvariant() : "";

        if (string.Equals(originCountry, destCountry, StringComparison.OrdinalIgnoreCase))
            return null; // Same country — always feasible

        var originContinent = GetContinent(originCountry);
        var destContinent = GetContinent(destCountry);

        // Tier 1: Islands or separate landmasses — physically impossible
        if (originContinent == "ISLAND" || destContinent == "ISLAND")
        {
            return new RiskFlag
            {
                Type = "MODE_INFEASIBLE",
                Probability = 1.0,
                Severity = "Critical",
                RationaleFacts = $"{mode} transport between {origin} and {destination} is not physically possible. At least one location is on an island. Results are unreliable — consider Ocean or Air.",
                Mitigations = ["Switch to Ocean or Air mode", "Use multimodal transport (sea + road)"]
            };
        }

        var originLandmass = GetLandmass(originContinent);
        var destLandmass = GetLandmass(destContinent);

        if (originLandmass != destLandmass)
        {
            return new RiskFlag
            {
                Type = "MODE_INFEASIBLE",
                Probability = 1.0,
                Severity = "Critical",
                RationaleFacts = $"{mode} transport between {origin} and {destination} is not physically possible. These locations are not connected by land. Results are unreliable — consider Ocean or Air.",
                Mitigations = ["Switch to Ocean or Air mode", "Use multimodal transport (sea + road)"]
            };
        }

        // Tier 2: Same landmass but different continents — impractical (e.g., China → Netherlands by road)
        if (originContinent != destContinent)
        {
            return new RiskFlag
            {
                Type = "MODE_IMPRACTICAL",
                Probability = 0.9,
                Severity = "High",
                RationaleFacts = $"{mode} transport between {origin} and {destination} spans different continents ({originContinent} → {destContinent}). While technically possible overland, this is highly unusual and unreliable. Ocean or Air is strongly recommended.",
                Mitigations = ["Switch to Ocean or Air mode", "Consider Rail for Eurasia corridor routes", "Use multimodal transport"]
            };
        }

        return null;
    }

    private static string GetContinent(string countryCode) => countryCode switch
    {
        "US" or "CA" or "MX" or "GT" or "BZ" or "HN" or "SV" or "NI" or "CR" or "PA" => "N.AMERICA",
        "BR" or "AR" or "CL" or "CO" or "PE" or "VE" or "EC" or "BO" or "PY" or "UY" or "GY" or "SR" => "S.AMERICA",
        "GB" or "IE" or "IS" => "ISLAND",
        "JP" or "TW" or "PH" or "ID" or "LK" or "FJ" or "NZ" => "ISLAND",
        "AU" or "PG" => "OCEANIA",
        "DE" or "FR" or "NL" or "BE" or "IT" or "ES" or "PT" or "SE" or "NO" or "DK" or "FI" or
        "PL" or "CZ" or "AT" or "CH" or "GR" or "RO" or "HU" or "BG" or "HR" or "SK" or "SI" or
        "LT" or "LV" or "EE" or "TR" or "RU" or "UA" or "BY" => "EUROPE",
        "CN" or "KR" or "MN" => "E.ASIA",
        "IN" or "PK" or "BD" or "NP" or "MM" => "S.ASIA",
        "TH" or "VN" or "KH" or "LA" or "MY" or "SG" or "BN" => "SE.ASIA",
        "AE" or "SA" or "QA" or "KW" or "BH" or "OM" or "JO" or "LB" or "IL" or "IQ" or "IR" or "KZ" or "UZ" => "M.EAST",
        "ZA" or "NG" or "KE" or "EG" or "MA" or "TN" or "GH" or "ET" or "TZ" or "CI" => "AFRICA",
        _ => "UNKNOWN"
    };

    private static string GetLandmass(string continent) => continent switch
    {
        "N.AMERICA" or "S.AMERICA" => "AMERICAS",
        "EUROPE" or "E.ASIA" or "S.ASIA" or "SE.ASIA" or "M.EAST" => "AFRO-EURASIA",
        "AFRICA" => "AFRO-EURASIA",
        "OCEANIA" => "OCEANIA",
        _ => continent
    };

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

    private async Task<SimulationResult> RunRateChangeAsync(SimulationRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var traces = new List<string>();

        _logger.LogInformation("Starting rate change simulation {RequestId} with seed={Seed}, runs={Runs}",
            request.RequestId, request.Seed, request.SimulationRuns);

        var shipment = request.Baseline.Shipment;
        var origin = shipment.Origin;
        var destination = shipment.Destination;
        var mode = shipment.Mode;

        var ratePrior = await _dataAdapter.GetRatePriorAsync(origin, destination, mode, ct)
            ?? new RatePrior { Origin = origin, Destination = destination, Mode = mode };

        double currentCost = ComputeBaseCost(request,
            await _dataAdapter.GetCarrierPriorAsync(shipment.Carrier ?? "UNKNOWN", mode, ct)
            ?? new CarrierPrior { CarrierCode = "UNKNOWN", Mode = mode });

        double rateChangePercent = ResolveRateChangePercent(request.ChangeSet);
        double newRateAmount = ResolveNewRateAmount(request.ChangeSet);
        double proposedCost = newRateAmount > 0 ? newRateAmount : currentCost * (1.0 + rateChangePercent / 100.0);
        double marginTarget = (double)(request.Baseline.Finance?.MarginTarget ?? 0.15m);

        traces.Add($"Rate change: current=${currentCost:F0}, proposed=${proposedCost:F0}, market benchmark=${ratePrior.MarketBenchmarkRate:F0}");

        var rng = new Random(request.Seed);
        int n = Math.Clamp(request.SimulationRuns, 1, 100_000);

        var costSamples = new double[n];
        var marginSamples = new double[n];

        for (int i = 0; i < n; i++)
        {
            // Market rate volatility
            double marketFluctuation = SampleNormal(rng, 0, ratePrior.RateVolatilityPercent);
            double effectiveMarketRate = ratePrior.MarketBenchmarkRate * ratePrior.SeasonalAdjustment * (1.0 + marketFluctuation);

            // Fuel surcharge variation
            double fuelVariation = SampleNormal(rng, ratePrior.FuelSurchargePercent, ratePrior.FuelSurchargePercent * 0.3);
            fuelVariation = Math.Max(0, fuelVariation);

            double simulatedCost = proposedCost * (1.0 + fuelVariation);
            costSamples[i] = simulatedCost;

            // Margin = (revenue - cost) / revenue, where revenue = cost / (1 - margin)
            // Approximate: how far is our proposed cost from market? margin = 1 - (proposedCost / sellingPrice)
            double sellingPrice = simulatedCost / (1.0 - marginTarget);
            double actualMargin = 1.0 - (simulatedCost / sellingPrice);

            // Market competitiveness affects realized margin
            double marketPressure = effectiveMarketRate / proposedCost;
            if (marketPressure < 1.0)
            {
                // Our rate is above market — margin pressure
                actualMargin *= marketPressure;
            }

            marginSamples[i] = Math.Max(-0.5, Math.Min(1.0, actualMargin));
        }

        sw.Stop();

        var costDist = BuildDistribution(costSamples);
        var marginDist = BuildDistribution(marginSamples);

        double marginErosionRisk = marginSamples.Count(m => m < marginTarget * 0.5) / (double)n;
        double rateAboveMarket = proposedCost > ratePrior.MarketBenchmarkRate ? 1.0 : 0.0;
        double overallRisk = Math.Min(1.0, (marginErosionRisk * 0.5) + (rateAboveMarket * 0.3) + (ratePrior.RateVolatilityPercent * 0.2));

        string outcome = overallRisk switch
        {
            < 0.2 => "Low risk. Proposed rate change appears financially sound.",
            < 0.5 => "Moderate risk. Rate competitiveness or margin may be affected.",
            < 0.8 => "High risk. Significant probability of margin erosion or uncompetitive pricing.",
            _ => "Critical risk. Strongly recommend reassessing rate structure."
        };

        var risks = BuildRateChangeRisks(marginErosionRisk, proposedCost, ratePrior, marginTarget);
        var recommendations = BuildRateChangeRecommendations(proposedCost, ratePrior, marginDist.P50, marginTarget);

        traces.Add($"Rate simulation completed in {sw.Elapsed.TotalMilliseconds:F1}ms");

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
                CostUsd = costDist,
                MarginPercent = marginDist
            },
            Risks = risks,
            Recommendations = recommendations,
            Traces = new SimulationTraces
            {
                Steps = traces,
                InternalState = new Dictionary<string, object>
                {
                    ["currentCost"] = currentCost,
                    ["proposedCost"] = proposedCost,
                    ["marketBenchmark"] = ratePrior.MarketBenchmarkRate,
                    ["rateChangePercent"] = rateChangePercent
                }
            }
        };
    }

    private async Task<SimulationResult> RunQuotationChangeAsync(SimulationRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var traces = new List<string>();

        _logger.LogInformation("Starting quotation simulation {RequestId} with seed={Seed}, runs={Runs}",
            request.RequestId, request.Seed, request.SimulationRuns);

        var shipment = request.Baseline.Shipment;
        var origin = shipment.Origin;
        var destination = shipment.Destination;
        var mode = shipment.Mode;

        var quotationPrior = await _dataAdapter.GetQuotationPriorAsync(origin, destination, mode, ct)
            ?? new QuotationPrior { Origin = origin, Destination = destination, Mode = mode };
        var ratePrior = await _dataAdapter.GetRatePriorAsync(origin, destination, mode, ct)
            ?? new RatePrior { Origin = origin, Destination = destination, Mode = mode };

        double baseCost = ComputeBaseCost(request,
            await _dataAdapter.GetCarrierPriorAsync(shipment.Carrier ?? "UNKNOWN", mode, ct)
            ?? new CarrierPrior { CarrierCode = "UNKNOWN", Mode = mode });

        double quotedMarginPercent = ResolveQuotedMargin(request.ChangeSet, request.Baseline.Finance?.MarginTarget);
        double competitorRate = ResolveCompetitorRate(request.ChangeSet);

        double quotedPrice = baseCost / (1.0 - quotedMarginPercent / 100.0);
        if (competitorRate <= 0) competitorRate = ratePrior.MarketBenchmarkRate;

        traces.Add($"Quotation: cost=${baseCost:F0}, quotedPrice=${quotedPrice:F0}, margin={quotedMarginPercent:F1}%, competitorRate=${competitorRate:F0}");

        var rng = new Random(request.Seed);
        int n = Math.Clamp(request.SimulationRuns, 1, 100_000);

        var costSamples = new double[n];
        var marginSamples = new double[n];
        var winSamples = new double[n];

        for (int i = 0; i < n; i++)
        {
            // Cost fluctuation from rate volatility
            double costFluctuation = SampleNormal(rng, 0, ratePrior.RateVolatilityPercent);
            double simulatedCost = baseCost * (1.0 + costFluctuation);
            simulatedCost = Math.Max(1.0, simulatedCost);
            costSamples[i] = simulatedCost;

            // Actual margin at quoted price
            double actualMargin = (quotedPrice - simulatedCost) / quotedPrice;
            marginSamples[i] = Math.Max(-0.5, Math.Min(1.0, actualMargin));

            // Win probability: base probability adjusted by price competitiveness
            double priceRatio = quotedPrice / competitorRate;
            double competitiveAdjustment = (1.0 - priceRatio) * quotationPrior.MarginSensitivity;
            // Add random competitor behavior
            double competitorNoise = SampleNormal(rng, 0, quotationPrior.AverageCompetitorDiscount);
            double winProb = quotationPrior.BaseWinProbability + competitiveAdjustment + competitorNoise;
            winProb = Math.Max(0.0, Math.Min(1.0, winProb));

            winSamples[i] = rng.NextDouble() < winProb ? 1.0 : 0.0;
        }

        sw.Stop();

        var costDist = BuildDistribution(costSamples);
        var marginDist = BuildDistribution(marginSamples);
        var winDist = BuildDistribution(winSamples);

        double avgWinProb = winSamples.Average();
        double avgMargin = marginSamples.Average();
        double marginBelowTarget = marginSamples.Count(m => m < quotedMarginPercent / 100.0 * 0.5) / (double)n;

        double overallRisk = Math.Min(1.0,
            ((1.0 - avgWinProb) * 0.4) + (marginBelowTarget * 0.35) + ((1.0 - quotationPrior.HistoricalConversionRate) * 0.25));

        string outcome = overallRisk switch
        {
            < 0.2 => "Low risk. Quotation is competitive with healthy margins.",
            < 0.5 => "Moderate risk. Win probability or margin sustainability may need attention.",
            < 0.8 => "High risk. Quotation may be uncompetitive or margins are at risk.",
            _ => "Critical risk. Strongly recommend adjusting quotation terms."
        };

        var risks = BuildQuotationRisks(avgWinProb, avgMargin, quotedMarginPercent, quotedPrice, competitorRate);
        var recommendations = BuildQuotationRecommendations(avgWinProb, avgMargin, quotedPrice, competitorRate, quotationPrior);

        traces.Add($"Quotation simulation completed in {sw.Elapsed.TotalMilliseconds:F1}ms");

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
                CostUsd = costDist,
                MarginPercent = marginDist,
                WinProbability = winDist
            },
            Risks = risks,
            Recommendations = recommendations,
            Traces = new SimulationTraces
            {
                Steps = traces,
                InternalState = new Dictionary<string, object>
                {
                    ["baseCost"] = baseCost,
                    ["quotedPrice"] = quotedPrice,
                    ["quotedMarginPercent"] = quotedMarginPercent,
                    ["competitorRate"] = competitorRate,
                    ["avgWinProbability"] = avgWinProb
                }
            }
        };
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
            P50 = Math.Round(p50, 4),
            P80 = Math.Round(p80, 4),
            P95 = Math.Round(p95, 4),
            Mean = Math.Round(mean, 4),
            StdDev = Math.Round(stdDev, 4),
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

    private static double ResolveRateChangePercent(ChangeSet change)
    {
        if (change.ChangeType == ChangeType.RateChange &&
            change.Parameters.TryGetValue("rateChangePercent", out var rcp))
        {
            if (rcp is double d) return d;
            if (double.TryParse(rcp?.ToString(), out var parsed)) return parsed;
        }
        return 0.0;
    }

    private static double ResolveNewRateAmount(ChangeSet change)
    {
        if (change.ChangeType == ChangeType.RateChange &&
            change.Parameters.TryGetValue("newRateAmount", out var nra))
        {
            if (nra is double d) return d;
            if (double.TryParse(nra?.ToString(), out var parsed)) return parsed;
        }
        return 0.0;
    }

    private static double ResolveQuotedMargin(ChangeSet change, decimal? baselineMarginTarget)
    {
        if (change.Parameters.TryGetValue("quotedMarginPercent", out var qm))
        {
            if (qm is double d) return d;
            if (double.TryParse(qm?.ToString(), out var parsed)) return parsed;
        }
        return baselineMarginTarget.HasValue ? (double)baselineMarginTarget.Value * 100.0 : 15.0;
    }

    private static double ResolveCompetitorRate(ChangeSet change)
    {
        if (change.Parameters.TryGetValue("competitorRate", out var cr))
        {
            if (cr is double d) return d;
            if (double.TryParse(cr?.ToString(), out var parsed)) return parsed;
        }
        return 0.0;
    }

    private static List<RiskFlag> BuildRateChangeRisks(double marginErosionRisk, double proposedCost,
        RatePrior ratePrior, double marginTarget)
    {
        var risks = new List<RiskFlag>();

        if (marginErosionRisk > 0.05)
        {
            risks.Add(new RiskFlag
            {
                Type = "MARGIN_EROSION",
                Probability = Math.Round(marginErosionRisk, 4),
                Severity = marginErosionRisk switch { > 0.5 => "Critical", > 0.3 => "High", > 0.15 => "Medium", _ => "Low" },
                RationaleFacts = $"Margin erosion risk: {marginErosionRisk:P1}. Target margin: {marginTarget:P0}. Rate volatility on this lane: {ratePrior.RateVolatilityPercent:P0}.",
                Mitigations = ["Lock in rates with a long-term contract", "Add fuel surcharge escalation clause", "Consider hedging instruments"]
            });
        }

        if (proposedCost > ratePrior.MarketBenchmarkRate * 1.1)
        {
            double overMarket = (proposedCost / ratePrior.MarketBenchmarkRate) - 1.0;
            risks.Add(new RiskFlag
            {
                Type = "RATE_ABOVE_MARKET",
                Probability = Math.Round(Math.Min(1.0, overMarket * 2), 4),
                Severity = overMarket > 0.2 ? "High" : "Medium",
                RationaleFacts = $"Proposed rate ${proposedCost:F0} is {overMarket:P0} above market benchmark ${ratePrior.MarketBenchmarkRate:F0}.",
                Mitigations = ["Renegotiate with carrier", "Request volume discount", "Consider alternative carriers on this lane"]
            });
        }

        if (ratePrior.RateVolatilityPercent > 0.15)
        {
            risks.Add(new RiskFlag
            {
                Type = "RATE_VOLATILITY",
                Probability = Math.Round(ratePrior.RateVolatilityPercent, 4),
                Severity = ratePrior.RateVolatilityPercent > 0.25 ? "High" : "Medium",
                RationaleFacts = $"Lane rate volatility is {ratePrior.RateVolatilityPercent:P0}. Seasonal adjustment factor: {ratePrior.SeasonalAdjustment:F2}.",
                Mitigations = ["Use forward rate agreements", "Diversify across carriers", "Build rate contingency buffer"]
            });
        }

        return risks;
    }

    private static List<Recommendation> BuildRateChangeRecommendations(double proposedCost,
        RatePrior ratePrior, double medianMargin, double marginTarget)
    {
        var recs = new List<Recommendation>();

        if (proposedCost > ratePrior.MarketBenchmarkRate)
        {
            double savings = proposedCost - ratePrior.MarketBenchmarkRate;
            recs.Add(new Recommendation
            {
                Option = "NegotiateToMarket",
                Description = $"Negotiate rate down to market benchmark (${ratePrior.MarketBenchmarkRate:F0}) for potential savings of ${savings:F0}.",
                ExpectedDeltas = new() { ["costUsd"] = -savings },
                Confidence = 0.7
            });
        }

        if (medianMargin < marginTarget)
        {
            recs.Add(new Recommendation
            {
                Option = "IncreaseSellingPrice",
                Description = "Increase selling price to restore target margin, or reduce costs through volume consolidation.",
                ExpectedDeltas = new() { ["marginPercent"] = marginTarget - medianMargin },
                Confidence = 0.6
            });
        }

        recs.Add(new Recommendation
        {
            Option = "LockInRate",
            Description = "Secure a long-term contract to protect against rate volatility on this lane.",
            ExpectedDeltas = new() { ["rateVolatility"] = -ratePrior.RateVolatilityPercent * 0.5 },
            Confidence = 0.65
        });

        return recs;
    }

    private static List<RiskFlag> BuildQuotationRisks(double avgWinProb, double avgMargin,
        double quotedMarginPercent, double quotedPrice, double competitorRate)
    {
        var risks = new List<RiskFlag>();

        if (avgWinProb < 0.3)
        {
            risks.Add(new RiskFlag
            {
                Type = "QUOTE_LOSS_RISK",
                Probability = Math.Round(1.0 - avgWinProb, 4),
                Severity = avgWinProb switch { < 0.1 => "Critical", < 0.2 => "High", _ => "Medium" },
                RationaleFacts = $"Win probability: {avgWinProb:P1}. Quoted price ${quotedPrice:F0} vs competitor ${competitorRate:F0}.",
                Mitigations = ["Reduce margin to improve price competitiveness", "Add value-added services to justify premium", "Offer volume-based discounts"]
            });
        }

        if (avgMargin < quotedMarginPercent / 100.0 * 0.8)
        {
            risks.Add(new RiskFlag
            {
                Type = "MARGIN_EROSION",
                Probability = Math.Round(Math.Min(1.0, (quotedMarginPercent / 100.0 - avgMargin) * 5), 4),
                Severity = avgMargin < 0.05 ? "High" : "Medium",
                RationaleFacts = $"Average realized margin: {avgMargin:P1}, below quoted target of {quotedMarginPercent:F1}%. Cost volatility may erode margins.",
                Mitigations = ["Build cost contingency into quotation", "Use cost-plus pricing model", "Add surcharge adjustment clauses"]
            });
        }

        if (quotedPrice > competitorRate * 1.15)
        {
            double premium = (quotedPrice / competitorRate) - 1.0;
            risks.Add(new RiskFlag
            {
                Type = "PRICE_UNCOMPETITIVE",
                Probability = Math.Round(Math.Min(1.0, premium * 3), 4),
                Severity = premium > 0.25 ? "High" : "Medium",
                RationaleFacts = $"Quoted price is {premium:P0} above competitor rate. This lane has price-sensitive buyers.",
                Mitigations = ["Match competitor pricing with reduced margin", "Differentiate on service level", "Bundle with other services"]
            });
        }

        return risks;
    }

    private static List<Recommendation> BuildQuotationRecommendations(double avgWinProb, double avgMargin,
        double quotedPrice, double competitorRate, QuotationPrior quotationPrior)
    {
        var recs = new List<Recommendation>();

        if (avgWinProb < 0.4 && quotedPrice > competitorRate)
        {
            double suggestedPrice = competitorRate * 0.98;
            recs.Add(new Recommendation
            {
                Option = "CompetitivePrice",
                Description = $"Price at ${suggestedPrice:F0} (2% below competitor) to improve win probability.",
                ExpectedDeltas = new() { ["winProbability"] = 0.15, ["costUsd"] = suggestedPrice - quotedPrice },
                Confidence = 0.7
            });
        }

        if (avgMargin > 0.20)
        {
            recs.Add(new Recommendation
            {
                Option = "ReduceMargin",
                Description = "Reduce margin to improve price competitiveness while maintaining profitability.",
                ExpectedDeltas = new() { ["marginPercent"] = -0.05, ["winProbability"] = 0.10 },
                Confidence = 0.65
            });
        }

        recs.Add(new Recommendation
        {
            Option = "ValueAddedQuote",
            Description = "Include value-added services (tracking, insurance, priority handling) to justify pricing premium.",
            ExpectedDeltas = new() { ["winProbability"] = 0.08 },
            Confidence = 0.5
        });

        if (quotationPrior.HistoricalConversionRate < 0.3)
        {
            recs.Add(new Recommendation
            {
                Option = "ReviewConversionStrategy",
                Description = $"Historical conversion on this lane is {quotationPrior.HistoricalConversionRate:P0}. Consider follow-up or discount strategy.",
                ExpectedDeltas = new() { ["winProbability"] = 0.05 },
                Confidence = 0.45
            });
        }

        return recs;
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
