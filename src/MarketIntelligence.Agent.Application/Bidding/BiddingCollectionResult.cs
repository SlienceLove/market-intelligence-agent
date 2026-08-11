namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Outcome of a bidding collection run. Notices are always returned newest-first
/// and deduplicated by fingerprint within the run itself; cross-run deduplication
/// is the ledger's responsibility.
/// </summary>
public sealed record BiddingCollectionResult
{
    public required string CollectionId { get; init; }

    public required BiddingCollectionStatus Status { get; init; }

    public IReadOnlyList<BiddingNotice> Notices { get; init; } = [];

    public string? FailureCode { get; init; }

    public string? FailureMessage { get; init; }

    public string? CorrelationId { get; init; }

    public BiddingFailureCategory ErrorCategory => BiddingFailureCatalog.Classify(FailureCode);

    public bool Succeeded => Status == BiddingCollectionStatus.Succeeded;

    public bool IsTerminal => Status != BiddingCollectionStatus.Running;

    public static BiddingCollectionResult Success(
        string collectionId,
        IReadOnlyList<BiddingNotice> notices,
        string? correlationId = null) =>
        new()
        {
            CollectionId = collectionId,
            Status = BiddingCollectionStatus.Succeeded,
            Notices = Normalize(notices),
            CorrelationId = correlationId
        };

    public static BiddingCollectionResult Failed(
        string collectionId,
        string failureCode,
        string? failureMessage = null,
        string? correlationId = null) =>
        new()
        {
            CollectionId = collectionId,
            Status = BiddingCollectionStatus.Failed,
            FailureCode = string.IsNullOrWhiteSpace(failureCode) ? "internal_error" : failureCode.Trim(),
            FailureMessage = BiddingFailureCatalog.SanitizeMessage(failureCode, failureMessage),
            CorrelationId = correlationId
        };

    public static BiddingCollectionResult Cancelled(string collectionId, string? correlationId = null) =>
        new()
        {
            CollectionId = collectionId,
            Status = BiddingCollectionStatus.Cancelled,
            FailureCode = "cancelled",
            CorrelationId = correlationId
        };

    public static BiddingCollectionResult Running(string collectionId, string? correlationId = null) =>
        new()
        {
            CollectionId = collectionId,
            Status = BiddingCollectionStatus.Running,
            CorrelationId = correlationId
        };

    /// <summary>
    /// Within-run deduplication and ordering. Notices arriving from multiple
    /// keyword queries against the same platform routinely overlap, so the first
    /// occurrence of each fingerprint wins and the rest are dropped.
    /// </summary>
    private static IReadOnlyList<BiddingNotice> Normalize(IReadOnlyList<BiddingNotice>? notices)
    {
        if (notices is null || notices.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = new List<BiddingNotice>(notices.Count);

        foreach (var notice in notices)
        {
            if (notice is not null && seen.Add(notice.Fingerprint))
            {
                deduplicated.Add(notice);
            }
        }

        deduplicated.Sort(static (left, right) => right.PublishedAt.CompareTo(left.PublishedAt));
        return deduplicated;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(CollectionId))
        {
            return "collection_id_required";
        }

        if (!Enum.IsDefined(Status))
        {
            return "invalid_status";
        }

        if (Status == BiddingCollectionStatus.Failed && string.IsNullOrWhiteSpace(FailureCode))
        {
            return "failure_code_required";
        }

        if (FailureCode is not null &&
            FailureCode.Length > BiddingContractLimits.MaxFailureCodeCharacters)
        {
            return "invalid_request";
        }

        foreach (var notice in Notices)
        {
            var noticeFailure = notice.Validate();
            if (noticeFailure is not null)
            {
                return noticeFailure;
            }
        }

        return Notices.Count > BiddingContractLimits.MaxResultsCeiling
            ? "notice_limit_exceeded"
            : null;
    }
}
