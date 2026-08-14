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
    /// Administrator-controlled directory containing <c>scheduled-plans.json</c>.
    /// The root comes from host configuration, while plan content cannot choose
    /// another path or file name.
    /// When unset the scheduler keeps the safe empty in-memory plan source.
    /// </summary>
    public string? PlanRoot { get; init; }

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
    /// Real platform identifiers enabled for live collection. Empty keeps the
    /// existing mock collector only. Supported values: <c>jszbtb.com</c>,
    /// <c>ggzy.gov.cn</c>, and <c>ccgp-jiangsu.gov.cn</c>.
    /// </summary>
    public IReadOnlyList<string> EnabledPlatforms { get; init; } = [];

    /// <summary>
    /// Minimum seconds between requests to the same platform. Default: 2
    /// </summary>
    public int MinimumIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// Maximum requests per second across all platforms. Default: 5
    /// </summary>
    public int GlobalQpsLimit { get; init; } = 5;
}
