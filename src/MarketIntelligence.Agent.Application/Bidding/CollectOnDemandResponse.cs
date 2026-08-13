namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>Result of an on-demand bidding collection.</summary>
public sealed record CollectOnDemandResponse
{
    public required int PlansExecuted { get; init; }
    public required int TotalNoticesCollected { get; init; }
    public required IReadOnlyList<PlanCollectionSummary> Plans { get; init; }

    /// <summary>Overall outcome: "success", "partial", or "failed".</summary>
    public required string Status { get; init; }
}

public sealed record PlanCollectionSummary
{
    public required string PlanId { get; init; }
    public required int NoticesCollected { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
}
