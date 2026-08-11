namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Safe-failure adapter registered by default so the pipeline never depends on a
/// real platform being wired up. Validates the request first so callers still get
/// precise validation feedback, then reports the missing configuration.
/// </summary>
public sealed class UnconfiguredBiddingNoticeCollector : IBiddingNoticeCollector
{
    public const string UnconfiguredPlatform = "unconfigured";

    public UnconfiguredBiddingNoticeCollector(string? sourcePlatform = null)
    {
        SourcePlatform = string.IsNullOrWhiteSpace(sourcePlatform)
            ? UnconfiguredPlatform
            : sourcePlatform.Trim();
    }

    public string SourcePlatform { get; }

    public bool IsConfigured => false;

    public Task<BiddingCollectionResult> CollectAsync(
        BiddingCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(
                BiddingCollectionResult.Cancelled(request.CollectionId, request.CorrelationId));
        }

        var validationFailure = request.Validate();
        if (validationFailure is not null)
        {
            return Task.FromResult(BiddingCollectionResult.Failed(
                request.CollectionId,
                validationFailure,
                correlationId: request.CorrelationId));
        }

        return Task.FromResult(BiddingCollectionResult.Failed(
            request.CollectionId,
            "bidding_source_not_configured",
            "Bidding source platform is not configured.",
            request.CorrelationId));
    }
}
