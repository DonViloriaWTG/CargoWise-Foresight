namespace CargoWise.Foresight.Core.Models;

public sealed record BaselineSnapshot
{
    public required ShipmentInfo Shipment { get; init; }
    public WorkflowInfo? Workflow { get; init; }
    public FinanceInfo? Finance { get; init; }
    public ComplianceInfo? Compliance { get; init; }
}

public sealed record ShipmentInfo
{
    public required string Id { get; init; }
    public required string Origin { get; init; }
    public required string Destination { get; init; }
    public string? Incoterm { get; init; }
    public required string Mode { get; init; } // "Ocean", "Air", "Road", "Rail"
    public string? Carrier { get; init; }
    public DateTimeOffset? PlannedEtd { get; init; }
    public DateTimeOffset? PlannedEta { get; init; }
    public string? ContainerType { get; init; }
    public bool Hazmat { get; init; }
    public decimal? Value { get; init; }
}

public sealed record WorkflowInfo
{
    public List<MilestoneInfo> Milestones { get; init; } = [];
    public List<DeadlineInfo> Deadlines { get; init; } = [];
    public List<SlaTarget> SlaTargets { get; init; } = [];
}

public sealed record MilestoneInfo
{
    public required string Name { get; init; }
    public DateTimeOffset? PlannedDate { get; init; }
    public bool Completed { get; init; }
}

public sealed record DeadlineInfo
{
    public required string Name { get; init; }
    public required DateTimeOffset DueDate { get; init; }
    public string? Consequence { get; init; }
}

public sealed record SlaTarget
{
    public required string Name { get; init; }
    public required double TargetDays { get; init; }
    public string? Metric { get; init; } // "TransitTime", "DeliveryDate", etc.
}

public sealed record FinanceInfo
{
    public List<RateLineItem> RateLineItems { get; init; } = [];
    public decimal? MarginTarget { get; init; }
    public string Currency { get; init; } = "USD";
}

public sealed record RateLineItem
{
    public required string Description { get; init; }
    public required decimal Amount { get; init; }
    public string? ChargeCode { get; init; }
}

public sealed record ComplianceInfo
{
    public List<string> Commodities { get; init; } = [];
    public List<string> CountriesInvolved { get; init; } = [];
}
