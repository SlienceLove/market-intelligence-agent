namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Placeholder parser that returns empty results.
/// Used when a platform is configured but has no real parser implementation yet.
/// </summary>
public sealed class UnconfiguredPlatformParser : IPlatformParser
{
    public UnconfiguredPlatformParser(string platformId)
    {
        PlatformId = platformId;
    }

    public string PlatformId { get; }

    public Uri BuildSearchUri(BiddingCollectionRequest request)
    {
        return new Uri("https://unconfigured.example/");
    }

    public Task<BiddingNotice[]> ParseAsync(
        string content,
        BiddingCollectionRequest request,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(Array.Empty<BiddingNotice>());
    }
}
