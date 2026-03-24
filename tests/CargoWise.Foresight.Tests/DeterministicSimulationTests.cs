using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using CargoWise.Foresight.Core.Models;
using CargoWise.Foresight.Core.Simulation;
using CargoWise.Foresight.Data.Mock;

namespace CargoWise.Foresight.Tests;

public class DeterministicSimulationTests
{
    private readonly MonteCarloSimulationEngine _engine;

    public DeterministicSimulationTests()
    {
        _engine = new MonteCarloSimulationEngine(
            new MockDataAdapter(),
            NullLogger<MonteCarloSimulationEngine>.Instance);
    }

    [Fact]
    public async Task SameSeed_ProducesSameResults()
    {
        var request = CreateCarrierSwapRequest(seed: 42);

        var result1 = await _engine.RunAsync(request);
        var result2 = await _engine.RunAsync(request);

        result1.Distributions.EtaDays!.P50.Should().Be(result2.Distributions.EtaDays!.P50);
        result1.Distributions.EtaDays.P80.Should().Be(result2.Distributions.EtaDays.P80);
        result1.Distributions.EtaDays.P95.Should().Be(result2.Distributions.EtaDays.P95);
        result1.Distributions.CostUsd!.P50.Should().Be(result2.Distributions.CostUsd!.P50);
        result1.Summary.OverallRiskScore.Should().Be(result2.Summary.OverallRiskScore);
    }

    [Fact]
    public async Task DifferentSeeds_ProduceDifferentResults()
    {
        var request1 = CreateCarrierSwapRequest(seed: 42);
        var request2 = CreateCarrierSwapRequest(seed: 99);

        var result1 = await _engine.RunAsync(request1);
        var result2 = await _engine.RunAsync(request2);

        // Very unlikely to be identical with different seeds
        (result1.Distributions.EtaDays!.P50 == result2.Distributions.EtaDays!.P50 &&
         result1.Distributions.EtaDays.P95 == result2.Distributions.EtaDays.P95)
            .Should().BeFalse("different seeds should produce different distributions");
    }

    [Fact]
    public async Task SimulationResult_HasRequiredFields()
    {
        var request = CreateCarrierSwapRequest(seed: 42);
        var result = await _engine.RunAsync(request);

        result.RequestId.Should().Be(request.RequestId);
        result.Summary.Should().NotBeNull();
        result.Summary.SimulationRuns.Should().Be(500);
        result.Summary.Seed.Should().Be(42);
        result.Summary.OverallRiskScore.Should().BeInRange(0.0, 1.0);
        result.Distributions.Should().NotBeNull();
        result.Distributions.EtaDays.Should().NotBeNull();
        result.Distributions.CostUsd.Should().NotBeNull();
        result.Risks.Should().NotBeNull();
        result.Recommendations.Should().NotBeNull();
    }

    [Fact]
    public async Task DistributionPercentiles_AreOrdered()
    {
        var request = CreateCarrierSwapRequest(seed: 42);
        var result = await _engine.RunAsync(request);

        var eta = result.Distributions.EtaDays!;
        eta.P50.Should().BeLessThanOrEqualTo(eta.P80);
        eta.P80.Should().BeLessThanOrEqualTo(eta.P95);

        var cost = result.Distributions.CostUsd!;
        cost.P50.Should().BeLessThanOrEqualTo(cost.P80);
        cost.P80.Should().BeLessThanOrEqualTo(cost.P95);
    }

    [Fact]
    public async Task Histogram_CountsSumToSimulationRuns()
    {
        var request = CreateCarrierSwapRequest(seed: 42);
        var result = await _engine.RunAsync(request);

        var totalEta = result.Distributions.EtaDays!.Histogram.Sum(b => b.Count);
        totalEta.Should().Be(500);

        var totalCost = result.Distributions.CostUsd!.Histogram.Sum(b => b.Count);
        totalCost.Should().Be(500);
    }

    [Fact]
    public async Task DepartureShift_IncreasesEta()
    {
        var baseline = CreateCarrierSwapRequest(seed: 42);
        var shifted = new SimulationRequest
        {
            RequestId = "shift-test",
            Seed = 42,
            Baseline = baseline.Baseline,
            ChangeSet = new ChangeSet
            {
                ChangeType = ChangeType.DepartureShift,
                Parameters = new Dictionary<string, object> { ["shiftDays"] = 5.0 }
            },
            SimulationRuns = 500
        };

        var baseResult = await _engine.RunAsync(baseline);
        var shiftResult = await _engine.RunAsync(shifted);

        shiftResult.Distributions.EtaDays!.P50.Should()
            .BeGreaterThan(baseResult.Distributions.EtaDays!.P50);
    }

    [Fact]
    public async Task HazmatShipment_IncreasesRisk()
    {
        var normalRequest = CreateCarrierSwapRequest(seed: 42);

        var hazmatBaseline = normalRequest.Baseline with
        {
            Shipment = normalRequest.Baseline.Shipment with { Hazmat = true }
        };
        var hazmatRequest = normalRequest with
        {
            RequestId = "hazmat-test",
            Baseline = hazmatBaseline
        };

        var normalResult = await _engine.RunAsync(normalRequest);
        var hazmatResult = await _engine.RunAsync(hazmatRequest);

        hazmatResult.Summary.OverallRiskScore.Should()
            .BeGreaterThanOrEqualTo(normalResult.Summary.OverallRiskScore);
    }

    [Fact]
    public async Task SmallRunCount_StillWorks()
    {
        var request = CreateCarrierSwapRequest(seed: 42) with { SimulationRuns = 10 };
        var result = await _engine.RunAsync(request);

        result.Summary.SimulationRuns.Should().Be(10);
        result.Distributions.EtaDays!.Histogram.Sum(b => b.Count).Should().Be(10);
    }

    [Fact]
    public void BuildDistribution_CorrectlyComputesStats()
    {
        double[] samples = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var dist = MonteCarloSimulationEngine.BuildDistribution(samples);

        dist.Mean.Should().Be(5.5);
        dist.P50.Should().BeApproximately(5.5, 0.01);
        dist.Histogram.Should().NotBeEmpty();
        dist.StdDev.Should().BeGreaterThan(0);
    }

    private static SimulationRequest CreateCarrierSwapRequest(int seed) => new()
    {
        RequestId = $"test-{seed}",
        Seed = seed,
        Baseline = new BaselineSnapshot
        {
            Shipment = new ShipmentInfo
            {
                Id = "SHP-TEST-001",
                Origin = "CNSHA",
                Destination = "USLAX",
                Mode = "Ocean",
                Carrier = "MSC",
                Hazmat = false,
                Value = 100000m
            },
            Workflow = new WorkflowInfo
            {
                SlaTargets = [new SlaTarget { Name = "Delivery", TargetDays = 18 }]
            },
            Finance = new FinanceInfo
            {
                RateLineItems =
                [
                    new RateLineItem { Description = "Ocean Freight", Amount = 2800 },
                    new RateLineItem { Description = "BAF", Amount = 350 }
                ],
                Currency = "USD"
            },
            Compliance = new ComplianceInfo
            {
                Commodities = ["electronics"],
                CountriesInvolved = ["CN", "US"]
            }
        },
        ChangeSet = new ChangeSet
        {
            ChangeType = ChangeType.CarrierSwap,
            Parameters = new Dictionary<string, object> { ["newCarrier"] = "COSCO" }
        },
        SimulationRuns = 500
    };

    // ── Rate Change tests ──

    [Fact]
    public async Task RateChange_ProducesCostAndMarginDistributions()
    {
        var request = CreateRateChangeRequest(seed: 42);
        var result = await _engine.RunAsync(request);

        result.RequestId.Should().Be(request.RequestId);
        result.Summary.Should().NotBeNull();
        result.Summary.OverallRiskScore.Should().BeInRange(0.0, 1.0);
        result.Distributions.CostUsd.Should().NotBeNull();
        result.Distributions.MarginPercent.Should().NotBeNull();
        result.Distributions.EtaDays.Should().BeNull("rate change does not simulate transit time");
    }

    [Fact]
    public async Task RateChange_SameSeed_Deterministic()
    {
        var request = CreateRateChangeRequest(seed: 42);

        var result1 = await _engine.RunAsync(request);
        var result2 = await _engine.RunAsync(request);

        result1.Distributions.CostUsd!.P50.Should().Be(result2.Distributions.CostUsd!.P50);
        result1.Distributions.MarginPercent!.P50.Should().Be(result2.Distributions.MarginPercent!.P50);
        result1.Summary.OverallRiskScore.Should().Be(result2.Summary.OverallRiskScore);
    }

    [Fact]
    public async Task RateChange_LargeIncrease_HigherRisk()
    {
        var smallChange = CreateRateChangeRequest(seed: 42, rateChangePercent: -5.0);
        var largeChange = CreateRateChangeRequest(seed: 42, rateChangePercent: 30.0);

        var smallResult = await _engine.RunAsync(smallChange);
        var largeResult = await _engine.RunAsync(largeChange);

        largeResult.Summary.OverallRiskScore.Should()
            .BeGreaterThanOrEqualTo(smallResult.Summary.OverallRiskScore,
                "a large rate increase should produce higher risk than a small decrease");
    }

    [Fact]
    public async Task RateChange_WithNewRateAmount_UsesAbsoluteRate()
    {
        var request = new SimulationRequest
        {
            RequestId = "rate-absolute-test",
            Seed = 42,
            Baseline = CreateBaselineSnapshot(),
            ChangeSet = new ChangeSet
            {
                ChangeType = ChangeType.RateChange,
                Parameters = new Dictionary<string, object> { ["newRateAmount"] = 4000.0 }
            },
            SimulationRuns = 100
        };

        var result = await _engine.RunAsync(request);

        result.Distributions.CostUsd.Should().NotBeNull();
        result.Distributions.CostUsd!.P50.Should().BeGreaterThan(3500, "cost should reflect the $4000 absolute rate");
    }

    // ── Quotation Change tests ──

    [Fact]
    public async Task QuotationChange_ProducesCostMarginAndWinDistributions()
    {
        var request = CreateQuotationChangeRequest(seed: 42);
        var result = await _engine.RunAsync(request);

        result.RequestId.Should().Be(request.RequestId);
        result.Summary.Should().NotBeNull();
        result.Summary.OverallRiskScore.Should().BeInRange(0.0, 1.0);
        result.Distributions.CostUsd.Should().NotBeNull();
        result.Distributions.MarginPercent.Should().NotBeNull();
        result.Distributions.WinProbability.Should().NotBeNull();
        result.Distributions.EtaDays.Should().BeNull("quotation change does not simulate transit time");
    }

    [Fact]
    public async Task QuotationChange_SameSeed_Deterministic()
    {
        var request = CreateQuotationChangeRequest(seed: 42);

        var result1 = await _engine.RunAsync(request);
        var result2 = await _engine.RunAsync(request);

        result1.Distributions.WinProbability!.P50.Should().Be(result2.Distributions.WinProbability!.P50);
        result1.Distributions.MarginPercent!.P50.Should().Be(result2.Distributions.MarginPercent!.P50);
        result1.Summary.OverallRiskScore.Should().Be(result2.Summary.OverallRiskScore);
    }

    [Fact]
    public async Task QuotationChange_HighMargin_LowerWinProbability()
    {
        var lowMargin = CreateQuotationChangeRequest(seed: 42, quotedMarginPercent: 5.0);
        var highMargin = CreateQuotationChangeRequest(seed: 42, quotedMarginPercent: 40.0);

        var lowResult = await _engine.RunAsync(lowMargin);
        var highResult = await _engine.RunAsync(highMargin);

        highResult.Distributions.WinProbability!.Mean.Should()
            .BeLessThanOrEqualTo(lowResult.Distributions.WinProbability!.Mean,
                "higher margin means higher price, which should reduce win probability");
    }

    [Fact]
    public async Task QuotationChange_HasRecommendations()
    {
        var request = CreateQuotationChangeRequest(seed: 42);
        var result = await _engine.RunAsync(request);

        result.Recommendations.Should().NotBeEmpty();
        result.Recommendations.Should().Contain(r => r.Option == "ValueAddedQuote");
    }

    // ── Helpers ──

    private static BaselineSnapshot CreateBaselineSnapshot() => new()
    {
        Shipment = new ShipmentInfo
        {
            Id = "SHP-TEST-001",
            Origin = "CNSHA",
            Destination = "USLAX",
            Mode = "Ocean",
            Carrier = "MSC",
            Hazmat = false,
            Value = 100000m
        },
        Workflow = new WorkflowInfo
        {
            SlaTargets = [new SlaTarget { Name = "Delivery", TargetDays = 18 }]
        },
        Finance = new FinanceInfo
        {
            RateLineItems =
            [
                new RateLineItem { Description = "Ocean Freight", Amount = 2800 },
                new RateLineItem { Description = "BAF", Amount = 350 }
            ],
            MarginTarget = 0.15m,
            Currency = "USD"
        },
        Compliance = new ComplianceInfo
        {
            Commodities = ["electronics"],
            CountriesInvolved = ["CN", "US"]
        }
    };

    private static SimulationRequest CreateRateChangeRequest(int seed, double rateChangePercent = -12.0) => new()
    {
        RequestId = $"rate-test-{seed}",
        Seed = seed,
        Baseline = CreateBaselineSnapshot(),
        ChangeSet = new ChangeSet
        {
            ChangeType = ChangeType.RateChange,
            Parameters = new Dictionary<string, object> { ["rateChangePercent"] = rateChangePercent }
        },
        SimulationRuns = 500
    };

    private static SimulationRequest CreateQuotationChangeRequest(int seed, double quotedMarginPercent = 20.0) => new()
    {
        RequestId = $"quote-test-{seed}",
        Seed = seed,
        Baseline = CreateBaselineSnapshot(),
        ChangeSet = new ChangeSet
        {
            ChangeType = ChangeType.QuotationChange,
            Parameters = new Dictionary<string, object>
            {
                ["quotedMarginPercent"] = quotedMarginPercent,
                ["competitorRate"] = 2800.0
            }
        },
        SimulationRuns = 500
    };
}
