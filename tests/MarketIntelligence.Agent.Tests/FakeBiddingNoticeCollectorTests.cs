using System.Reflection;
using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Tests;

public sealed class FakeBiddingNoticeCollectorTests
{
    private static BiddingCollectionRequest Request(
        params string[] keywords) =>
        new()
        {
            CollectionId = "collect-fake-1",
            Keywords = keywords.Length == 0 ? ["智慧园区"] : keywords,
            CorrelationId = "corr-fake-1"
        };

    [Fact]
    public async Task Fake_collector_returns_deterministic_notices_for_the_same_request()
    {
        var collector = new FakeBiddingNoticeCollector();

        var first = await collector.CollectAsync(Request("智慧园区", "数据中心"));
        var second = await collector.CollectAsync(Request("智慧园区", "数据中心"));

        Assert.True(first.Succeeded);
        Assert.Equal(4, first.Notices.Count);
        Assert.Equal(
            first.Notices.Select(notice => notice.Fingerprint),
            second.Notices.Select(notice => notice.Fingerprint));
        Assert.All(first.Notices, notice => Assert.Null(notice.Validate()));
        Assert.Equal("corr-fake-1", first.CorrelationId);
        Assert.Null(first.Validate());
    }

    [Fact]
    public async Task Fake_collector_honours_max_results_and_time_window()
    {
        var reference = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
        var collector = new FakeBiddingNoticeCollector(noticesPerKeyword: 5, referenceInstant: reference);

        var capped = await collector.CollectAsync(Request("智慧园区") with { MaxResults = 2 });
        Assert.Equal(2, capped.Notices.Count);

        var windowed = await collector.CollectAsync(Request("智慧园区") with
        {
            FromDate = reference.AddDays(-1),
            ToDate = reference
        });

        Assert.Equal(2, windowed.Notices.Count);
        Assert.All(windowed.Notices, notice => Assert.True(notice.PublishedAt >= reference.AddDays(-1)));
    }

    [Fact]
    public async Task Fake_collector_reports_empty_result_and_propagates_cancellation()
    {
        var empty = new FakeBiddingNoticeCollector(noticesPerKeyword: 0);
        var emptyResult = await empty.CollectAsync(Request());

        Assert.Equal(BiddingCollectionStatus.Failed, emptyResult.Status);
        Assert.Equal("empty_collection_result", emptyResult.FailureCode);
        Assert.Equal(BiddingFailureCategory.EmptyResult, emptyResult.ErrorCategory);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var cancelled = await new FakeBiddingNoticeCollector()
            .CollectAsync(Request(), cancellation.Token);

        Assert.Equal(BiddingCollectionStatus.Cancelled, cancelled.Status);
        Assert.Equal(BiddingFailureCategory.Cancelled, cancelled.ErrorCategory);
    }

    [Fact]
    public async Task Fake_collector_validates_requests_before_producing_notices()
    {
        var collector = new FakeBiddingNoticeCollector();

        var invalid = await collector.CollectAsync(Request() with { Keywords = [] });

        Assert.Equal("keyword_required", invalid.FailureCode);
        Assert.Equal(BiddingFailureCategory.Validation, invalid.ErrorCategory);
        Assert.Empty(invalid.Notices);
        await Assert.ThrowsAsync<ArgumentNullException>(() => collector.CollectAsync(null!));
    }

    [Fact]
    public void Fake_collector_holds_no_http_dependency_so_it_cannot_reach_the_network()
    {
        var fieldTypes = typeof(FakeBiddingNoticeCollector)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(field => field.FieldType)
            .ToArray();

        Assert.DoesNotContain(typeof(HttpClient), fieldTypes);
        Assert.DoesNotContain(typeof(HttpMessageHandler), fieldTypes);
        Assert.DoesNotContain(typeof(IHttpClientFactory), fieldTypes);

        Assert.True(new FakeBiddingNoticeCollector().IsConfigured);
    }

    [Fact]
    public async Task Unconfigured_collector_reports_configuration_gap_after_validating_the_request()
    {
        var collector = new UnconfiguredBiddingNoticeCollector();

        Assert.False(collector.IsConfigured);

        var unconfigured = await collector.CollectAsync(Request());
        Assert.Equal("bidding_source_not_configured", unconfigured.FailureCode);
        Assert.Equal(BiddingFailureCategory.ProviderUnavailable, unconfigured.ErrorCategory);
        Assert.False(BiddingFailureCatalog.IsRetryable(unconfigured.FailureCode));
        Assert.Empty(unconfigured.Notices);

        var invalid = await collector.CollectAsync(Request() with { CollectionId = " " });
        Assert.Equal("collection_id_required", invalid.FailureCode);
    }
}
