namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Deterministic in-memory collector used to prove the keyword to notification
/// chain end to end without touching a real platform. Holds no
/// <see cref="HttpClient"/> and issues no network request of any kind.
/// </summary>
public sealed class FakeBiddingNoticeCollector : IBiddingNoticeCollector
{
    public const string FakePlatform = "fake.bidding.local";

    private readonly IReadOnlyList<BiddingNotice> _catalog;
    private readonly int _noticesPerKeyword;
    private readonly DateTimeOffset _referenceInstant;

    public FakeBiddingNoticeCollector(
        IReadOnlyList<BiddingNotice>? catalog = null,
        int noticesPerKeyword = 2,
        DateTimeOffset? referenceInstant = null,
        string? sourcePlatform = null)
    {
        _catalog = catalog ?? [];
        _noticesPerKeyword = Math.Clamp(noticesPerKeyword, 0, BiddingContractLimits.MaxResultsCeiling);
        _referenceInstant = referenceInstant ?? new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        SourcePlatform = string.IsNullOrWhiteSpace(sourcePlatform) ? FakePlatform : sourcePlatform.Trim();
    }

    public string SourcePlatform { get; }

    public bool IsConfigured => true;

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

        return Task.FromResult(Collect(request));
    }

    private BiddingCollectionResult Collect(BiddingCollectionRequest request)
    {
        var matched = new List<BiddingNotice>();

        foreach (var keyword in request.Keywords)
        {
            matched.AddRange(MatchCatalog(keyword));
            matched.AddRange(Synthesize(keyword));
        }

        // No Take() here: the cap belongs after dedupe and ordering, so it is
        // handed to Success rather than applied to the raw matched set.
        var filtered = matched
            .Where(notice => InWindow(notice, request))
            .ToList();

        return filtered.Count == 0
            ? BiddingCollectionResult.Failed(
                request.CollectionId,
                "empty_collection_result",
                "No notice matched the supplied keywords.",
                request.CorrelationId)
            : BiddingCollectionResult.Success(
                request.CollectionId,
                filtered,
                request.CorrelationId,
                request.MaxResults);
    }

    private IEnumerable<BiddingNotice> MatchCatalog(string keyword) =>
        _catalog.Where(notice =>
            notice.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            notice.Publisher.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds notices whose fingerprints are a pure function of keyword and index,
    /// so repeated runs with the same request produce byte-identical output and the
    /// dedupe ledger can be exercised without any live source.
    /// </summary>
    private IEnumerable<BiddingNotice> Synthesize(string keyword)
    {
        for (var index = 0; index < _noticesPerKeyword; index++)
        {
            var title = $"{keyword} 项目招标公告 第{index + 1}批";
            var noticeUrl = $"https://{FakePlatform}/notice/{Uri.EscapeDataString(keyword)}/{index}";

            yield return new BiddingNotice
            {
                Title = title,
                Publisher = $"{keyword}采购中心",
                PublishedAt = _referenceInstant.AddDays(-index),
                NoticeUrl = noticeUrl,
                SourcePlatform = SourcePlatform,
                Fingerprint = BiddingNoticeFingerprint.Compute(SourcePlatform, noticeUrl, title),
                Region = "测试地区",
                Industry = "测试行业",
                AmountRange = "100万-500万"
            };
        }
    }

    private static bool InWindow(BiddingNotice notice, BiddingCollectionRequest request)
    {
        if (request.FromDate is { } from && notice.PublishedAt < from)
        {
            return false;
        }

        return request.ToDate is not { } to || notice.PublishedAt <= to;
    }
}
