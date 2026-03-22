using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using CargoWise.Foresight.Core.Models;

namespace CargoWise.Foresight.Tests;

public class ContractSchemaValidationTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    [Theory]
    [InlineData("carrier-swap-scenario.json")]
    [InlineData("departure-shift-hazmat.json")]
    [InlineData("route-change-diversion.json")]
    public void SampleScenario_DeserializesCorrectly(string filename)
    {
        var path = Path.Combine(FindSamplesDir(), "scenarios", filename);
        var json = File.ReadAllText(path);

        var request = JsonSerializer.Deserialize<SimulationRequest>(json, JsonOpts);

        request.Should().NotBeNull();
        request!.RequestId.Should().NotBeNullOrWhiteSpace();
        request.Baseline.Should().NotBeNull();
        request.Baseline.Shipment.Should().NotBeNull();
        request.Baseline.Shipment.Id.Should().NotBeNullOrWhiteSpace();
        request.Baseline.Shipment.Origin.Should().NotBeNullOrWhiteSpace();
        request.Baseline.Shipment.Destination.Should().NotBeNullOrWhiteSpace();
        request.Baseline.Shipment.Mode.Should().NotBeNullOrWhiteSpace();
        request.ChangeSet.Should().NotBeNull();
        request.SimulationRuns.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SimulationResult_SerializesAndDeserializes_Roundtrip()
    {
        var result = new SimulationResult
        {
            RequestId = "roundtrip-test",
            Summary = new SimulationSummary
            {
                Outcome = "Low risk",
                OverallRiskScore = 0.15,
                SimulationRuns = 500,
                Seed = 42,
                DurationMs = 123.45
            },
            Distributions = new DistributionSet
            {
                EtaDays = new Distribution
                {
                    P50 = 14.0, P80 = 16.5, P95 = 19.2, Mean = 14.8, StdDev = 2.3,
                    Histogram =
                    [
                        new HistogramBucket { LowerBound = 10, UpperBound = 15, Count = 300 },
                        new HistogramBucket { LowerBound = 15, UpperBound = 20, Count = 200 }
                    ]
                },
                CostUsd = new Distribution
                {
                    P50 = 3500, P80 = 4200, P95 = 5100, Mean = 3700, StdDev = 600,
                    Histogram =
                    [
                        new HistogramBucket { LowerBound = 3000, UpperBound = 4000, Count = 350 },
                        new HistogramBucket { LowerBound = 4000, UpperBound = 5500, Count = 150 }
                    ]
                }
            },
            Risks =
            [
                new RiskFlag
                {
                    Type = "SLA_BREACH", Probability = 0.23, Severity = "Medium",
                    RationaleFacts = "Based on historical data", Mitigations = ["Use express"]
                }
            ],
            Recommendations =
            [
                new Recommendation
                {
                    Option = "ExpressMode", Description = "Switch to air", Confidence = 0.75,
                    ExpectedDeltas = new() { ["etaDays"] = -5.0, ["costUsd"] = 1400 }
                }
            ]
        };

        var json = JsonSerializer.Serialize(result, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<SimulationResult>(json, JsonOpts);

        deserialized.Should().NotBeNull();
        deserialized!.RequestId.Should().Be("roundtrip-test");
        deserialized.Summary.OverallRiskScore.Should().Be(0.15);
        deserialized.Distributions.EtaDays!.P50.Should().Be(14.0);
        deserialized.Risks.Should().HaveCount(1);
        deserialized.Risks[0].Type.Should().Be("SLA_BREACH");
        deserialized.Recommendations.Should().HaveCount(1);
    }

    [Fact]
    public void ExplanationRequest_SerializesCorrectly()
    {
        var request = new ExplanationRequest
        {
            RequestId = "explain-test",
            SimulationResult = new SimulationResult
            {
                RequestId = "sim-test",
                Summary = new SimulationSummary
                {
                    Outcome = "Test", OverallRiskScore = 0.1,
                    SimulationRuns = 100, Seed = 1, DurationMs = 10
                },
                Distributions = new DistributionSet(),
                Risks = [],
                Recommendations = []
            },
            Audience = "manager",
            Tone = "concise"
        };

        var json = JsonSerializer.Serialize(request, JsonOpts);
        var deserialized = JsonSerializer.Deserialize<ExplanationRequest>(json, JsonOpts);

        deserialized.Should().NotBeNull();
        deserialized!.Audience.Should().Be("manager");
        deserialized.Tone.Should().Be("concise");
    }

    [Fact]
    public void ChangeType_SerializesAsString()
    {
        var changeSet = new ChangeSet
        {
            ChangeType = ChangeType.CarrierSwap,
            Parameters = new Dictionary<string, object> { ["newCarrier"] = "COSCO" }
        };

        var json = JsonSerializer.Serialize(changeSet, JsonOpts);

        // changeType should serialize as a string (either PascalCase or camelCase depending on serializer)
        json.Should().ContainAny("\"CarrierSwap\"", "\"carrierSwap\"");
    }

    [Fact]
    public void AllChangeTypes_AreValid()
    {
        var values = Enum.GetValues<ChangeType>();
        values.Should().HaveCountGreaterOrEqualTo(5);
        values.Should().Contain(ChangeType.CarrierSwap);
        values.Should().Contain(ChangeType.RouteChange);
        values.Should().Contain(ChangeType.DepartureShift);
        values.Should().Contain(ChangeType.CustomsFilingChange);
        values.Should().Contain(ChangeType.PaymentTermChange);
    }

    private static string FindSamplesDir()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        while (dir != null)
        {
            var samples = Path.Combine(dir.FullName, "samples");
            if (Directory.Exists(samples)) return samples;
            // Check parent for solution root
            var sln = Directory.GetFiles(dir.FullName, "*.sln");
            if (sln.Length > 0)
            {
                samples = Path.Combine(dir.FullName, "samples");
                if (Directory.Exists(samples)) return samples;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Cannot find samples directory. Ensure tests run from solution root.");
    }
}
