namespace MarketIntelligence.Agent.Infrastructure.Bidding;

public sealed record BiddingOptions
{
    /// <summary>
    /// Root directory where the notice ledger file is persisted. Must be
    /// configured for the ledger to operate. Relative paths are resolved against
    /// the process working directory at startup.
    /// </summary>
    public string? LedgerRoot { get; init; }

    /// <summary>
    /// How long to retain ledger entries after last activity (notified timestamp,
    /// or first-seen if never notified). Defaults to 90 days.
    /// </summary>
    public TimeSpan RetentionWindow { get; init; } = TimeSpan.FromDays(90);

    /// <summary>
    /// Collector rate limiting and behavior settings.
    /// </summary>
    public CollectorSettings Collector { get; init; } = new();
}

public sealed record CollectorSettings
{
    /// <summary>
    /// Minimum seconds between requests to the same platform. Default: 2
    /// </summary>
    public int MinimumIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Maximum requests per second across all platforms. Default: 5
    /// </summary>
    public int GlobalQpsLimit { get; init; } = 5;
}
