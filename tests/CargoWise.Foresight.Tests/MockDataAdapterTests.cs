using FluentAssertions;
using CargoWise.Foresight.Data.Mock;

namespace CargoWise.Foresight.Tests;

public class MockDataAdapterTests
{
    private readonly MockDataAdapter _adapter = new();

    [Theory]
    [InlineData("MAERSK", "Ocean")]
    [InlineData("MSC", "Ocean")]
    [InlineData("COSCO", "Ocean")]
    [InlineData("FEDEX_AIR", "Air")]
    public async Task GetCarrierPrior_ReturnsData_ForKnownCarriers(string code, string mode)
    {
        var result = await _adapter.GetCarrierPriorAsync(code, mode);
        result.Should().NotBeNull();
        result!.CarrierCode.Should().Be(code);
        result.ReliabilityScore.Should().BeInRange(0, 1);
    }

    [Fact]
    public async Task GetCarrierPrior_ReturnsNull_ForUnknownCarrier()
    {
        var result = await _adapter.GetCarrierPriorAsync("NONEXISTENT", "Ocean");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetRoutePrior_ReturnsData_ForKnownRoute()
    {
        var result = await _adapter.GetRoutePriorAsync("CNSHA", "USLAX", "Ocean");
        result.Should().NotBeNull();
        result!.BaseTransitDays.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetCustomsPrior_ReturnsData_ForKnownCountry()
    {
        var result = await _adapter.GetCustomsPriorAsync("US");
        result.Should().NotBeNull();
        result!.BaseHoldProbability.Should().BeInRange(0, 1);
    }

    [Fact]
    public async Task GetDemurragePrior_ReturnsData_ForAllModes()
    {
        foreach (var mode in new[] { "Ocean", "Air", "Road", "Rail" })
        {
            var result = await _adapter.GetDemurragePriorAsync(mode);
            result.Should().NotBeNull($"mode {mode} should have demurrage data");
            result!.DailyRate.Should().BeGreaterThan(0);
        }
    }
}
