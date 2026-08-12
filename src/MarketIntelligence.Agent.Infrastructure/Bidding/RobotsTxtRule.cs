namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Represents a parsed robots.txt rule group for a specific user-agent.
/// Implements RFC 9309 core directives: User-agent, Disallow, Allow, and Crawl-delay.
/// </summary>
public sealed record RobotsTxtRule
{
    /// <summary>
    /// The user-agent pattern this rule applies to. "*" is a wildcard matching all agents.
    /// </summary>
    public required string UserAgent { get; init; }

    /// <summary>
    /// Paths that are disallowed for this user-agent. Empty array means no restrictions.
    /// </summary>
    public required string[] Disallow { get; init; }

    /// <summary>
    /// Paths that are explicitly allowed, overriding any matching Disallow rules.
    /// Empty array means no explicit allowances.
    /// </summary>
    public required string[] Allow { get; init; }

    /// <summary>
    /// Crawl delay in seconds, if specified. Null means no delay is specified.
    /// </summary>
    public int? CrawlDelay { get; init; }
}
