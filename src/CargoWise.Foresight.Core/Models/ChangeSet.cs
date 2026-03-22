using System.Text.Json.Serialization;

namespace CargoWise.Foresight.Core.Models;

public sealed record ChangeSet
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ChangeType ChangeType { get; init; }
    public required Dictionary<string, object> Parameters { get; init; } = [];
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChangeType
{
    CarrierSwap,
    RouteChange,
    DepartureShift,
    CustomsFilingChange,
    PaymentTermChange
}
