namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>Request to trigger on-demand bidding collection.</summary>
public sealed record CollectOnDemandRequest
{
    /// <summary>
    /// Plan identifiers to collect. If empty, collects for all registered plans.
    /// </summary>
    public IReadOnlyList<string> PlanIds { get; init; } = [];

    /// <summary>
    /// Override the collection date (default: today UTC).
    /// Format: yyyy-MM-dd
    /// </summary>
    public DateOnly? AsOf { get; init; }
}
