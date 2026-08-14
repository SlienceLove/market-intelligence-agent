using Xunit;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Opt-in marker for real public-platform smoke tests. Enable only when outbound
/// access is intended: <c>MI_SMOKE_BIDDING=1</c>.
/// </summary>
public sealed class RequiresRealBiddingPlatformsFactAttribute : FactAttribute
{
    public RequiresRealBiddingPlatformsFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("MI_SMOKE_BIDDING"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "MI_SMOKE_BIDDING=1 not set; real bidding-platform smoke skipped.";
        }
    }
}
