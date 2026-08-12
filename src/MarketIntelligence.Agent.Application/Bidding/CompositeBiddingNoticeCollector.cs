using Microsoft.Extensions.Logging;

namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Aggregates multiple platform collectors and collects from all in parallel.
/// This is the top-level collector used by the coordinator.
/// </summary>
public sealed class CompositeBiddingNoticeCollector : IBiddingNoticeCollector
{
    private readonly IEnumerable<IBiddingNoticeCollector> _collectors;
    private readonly ILogger<CompositeBiddingNoticeCollector> _logger;

    public CompositeBiddingNoticeCollector(
        IEnumerable<IBiddingNoticeCollector> collectors,
        ILogger<CompositeBiddingNoticeCollector> logger)
    {
        _collectors = collectors ?? throw new ArgumentNullException(nameof(collectors));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string SourcePlatform => "composite";

    public bool IsConfigured => true;

    public async Task<BiddingCollectionResult> CollectAsync(
        BiddingCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var collectorList = _collectors.ToList();

        if (collectorList.Count == 0)
        {
            _logger.LogWarning("No platform collectors registered");
            return BiddingCollectionResult.Failed(
                request.CollectionId,
                "collector_error",
                "No platform collectors available",
                request.CorrelationId);
        }

        _logger.LogInformation(
            "Starting composite collection from {Count} platform(s) for collection {CollectionId}",
            collectorList.Count,
            request.CollectionId);

        // Collect from all platforms in parallel
        var tasks = collectorList.Select(collector =>
            collector.CollectAsync(request, cancellationToken));

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // Aggregate all notices
        var allNotices = results
            .Where(r => r.Succeeded)
            .SelectMany(r => r.Notices)
            .ToList();

        _logger.LogInformation(
            "Composite collection completed: {SuccessCount}/{TotalCount} platforms succeeded, {NoticeCount} total notices",
            results.Count(r => r.Succeeded),
            results.Length,
            allNotices.Count);

        // Determine aggregate outcome
        var outcome = DetermineOutcome(results);
        var errorMessage = AggregateErrorMessages(results);

        if (allNotices.Count == 0 && !results.Any(r => r.Succeeded))
        {
            _logger.LogWarning(
                "All {Count} platform collectors failed for collection {CollectionId}",
                collectorList.Count,
                request.CollectionId);

            // Special case: if all were cancelled, return cancelled
            if (results.All(r => r.Status == BiddingCollectionStatus.Cancelled))
            {
                return BiddingCollectionResult.Cancelled(
                    request.CollectionId,
                    request.CorrelationId);
            }

            return BiddingCollectionResult.Failed(
                request.CollectionId,
                outcome,
                errorMessage,
                request.CorrelationId);
        }

        // Return success with all collected notices
        // The Success factory method handles deduplication and ordering
        return BiddingCollectionResult.Success(
            request.CollectionId,
            allNotices,
            request.CorrelationId,
            request.MaxResults);
    }

    /// <summary>
    /// Determines the aggregate outcome code from individual collector results.
    /// - If any succeeded: success (handled in main method)
    /// - If all failed with same code: use that code
    /// - If mixed failures: collector_error
    /// </summary>
    private static string DetermineOutcome(BiddingCollectionResult[] results)
    {
        var failedResults = results.Where(r => !r.Succeeded).ToList();

        if (failedResults.Count == 0)
        {
            return "success"; // Shouldn't reach here given calling context
        }

        // All failed - check if they share the same failure code
        var distinctCodes = failedResults
            .Select(r => r.FailureCode ?? "internal_error")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctCodes.Count == 1)
        {
            return distinctCodes[0];
        }

        // Mixed failure codes
        return "collector_error";
    }

    /// <summary>
    /// Aggregates error messages from failed collectors.
    /// </summary>
    private static string? AggregateErrorMessages(BiddingCollectionResult[] results)
    {
        var messages = results
            .Where(r => !r.Succeeded && !string.IsNullOrWhiteSpace(r.FailureMessage))
            .Select(r => r.FailureMessage!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (messages.Count == 0)
        {
            return null;
        }

        if (messages.Count == 1)
        {
            return messages[0];
        }

        return $"Multiple failures: {string.Join("; ", messages)}";
    }
}
