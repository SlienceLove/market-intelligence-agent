namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Collects public bidding notices from a single source platform.
/// One implementation per platform; the platform identity is exposed so the
/// registry can route a request and so failures can be attributed.
/// </summary>
/// <remarks>
/// Implementations must never throw for expected failures. Validation, policy,
/// rate-limit, and provider problems are reported as a failed
/// <see cref="BiddingCollectionResult"/> carrying a catalog failure code.
/// </remarks>
public interface IBiddingNoticeCollector
{
    /// <summary>
    /// Stable platform identifier, normalized the same way fingerprints
    /// normalize their platform segment.
    /// </summary>
    string SourcePlatform { get; }

    /// <summary>
    /// True when the collector has everything it needs to run. A false value
    /// means callers should expect <c>bidding_source_not_configured</c>.
    /// </summary>
    bool IsConfigured { get; }

    Task<BiddingCollectionResult> CollectAsync(
        BiddingCollectionRequest request,
        CancellationToken cancellationToken = default);
}
