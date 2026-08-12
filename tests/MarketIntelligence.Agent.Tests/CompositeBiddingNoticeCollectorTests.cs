using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketIntelligence.Agent.Tests;

public sealed class CompositeBiddingNoticeCollectorTests
{
    [Fact]
    public async Task CollectAsync_aggregates_notices_from_multiple_successful_collectors()
    {
        // Arrange
        var notice1 = CreateNotice("Notice 1", "https://platform1.example/1");
        var notice2 = CreateNotice("Notice 2", "https://platform2.example/2");
        var notice3 = CreateNotice("Notice 3", "https://platform1.example/3");

        var collector1 = CreateMockCollector("platform1", BiddingCollectionResult.Success(
            "test-1", [notice1, notice3]));
        var collector2 = CreateMockCollector("platform2", BiddingCollectionResult.Success(
            "test-2", [notice2]));

        var composite = new CompositeBiddingNoticeCollector(
            [collector1, collector2],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-1");

        // Act
        var result = await composite.CollectAsync(request);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Succeeded, result.Status);
        Assert.Equal(3, result.Notices.Count);
        Assert.Contains(result.Notices, n => n.Title == "Notice 1");
        Assert.Contains(result.Notices, n => n.Title == "Notice 2");
        Assert.Contains(result.Notices, n => n.Title == "Notice 3");
    }

    [Fact]
    public async Task CollectAsync_returns_success_when_any_collector_succeeds()
    {
        // Arrange
        var notice1 = CreateNotice("Notice 1", "https://platform1.example/1");

        var collector1 = CreateMockCollector("platform1", BiddingCollectionResult.Success(
            "test-1", [notice1]));
        var collector2 = CreateMockCollector("platform2", BiddingCollectionResult.Failed(
            "test-2", "timeout", "Request timed out"));

        var composite = new CompositeBiddingNoticeCollector(
            [collector1, collector2],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-2");

        // Act
        var result = await composite.CollectAsync(request);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Succeeded, result.Status);
        Assert.Single(result.Notices);
        Assert.Equal("Notice 1", result.Notices[0].Title);
    }

    [Fact]
    public async Task CollectAsync_returns_same_failure_code_when_all_fail_with_same_code()
    {
        // Arrange
        var collector1 = CreateMockCollector("platform1", BiddingCollectionResult.Failed(
            "test-1", "timeout", "Platform 1 timed out"));
        var collector2 = CreateMockCollector("platform2", BiddingCollectionResult.Failed(
            "test-2", "timeout", "Platform 2 timed out"));

        var composite = new CompositeBiddingNoticeCollector(
            [collector1, collector2],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-3");

        // Act
        var result = await composite.CollectAsync(request);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("timeout", result.FailureCode);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_returns_collector_error_when_all_fail_with_different_codes()
    {
        // Arrange
        var collector1 = CreateMockCollector("platform1", BiddingCollectionResult.Failed(
            "test-1", "timeout", "Platform 1 timed out"));
        var collector2 = CreateMockCollector("platform2", BiddingCollectionResult.Failed(
            "test-2", "rate_limited", "Platform 2 rate limited"));
        var collector3 = CreateMockCollector("platform3", BiddingCollectionResult.Failed(
            "test-3", "provider_unavailable", "Platform 3 unavailable"));

        var composite = new CompositeBiddingNoticeCollector(
            [collector1, collector2, collector3],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-4");

        // Act
        var result = await composite.CollectAsync(request);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("collector_error", result.FailureCode);
        Assert.Empty(result.Notices);
        Assert.NotNull(result.FailureMessage);
        Assert.Contains("Multiple failures", result.FailureMessage);
    }

    [Fact]
    public async Task CollectAsync_returns_error_when_no_collectors_registered()
    {
        // Arrange
        var composite = new CompositeBiddingNoticeCollector(
            Array.Empty<IBiddingNoticeCollector>(),
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-5");

        // Act
        var result = await composite.CollectAsync(request);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("collector_error", result.FailureCode);
        Assert.Contains("No platform collectors available", result.FailureMessage ?? "");
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_deduplicates_notices_across_platforms()
    {
        // Arrange: same fingerprint appears in two collectors
        var duplicateNotice1 = CreateNotice("Duplicate Notice", "https://platform.example/same", "2024-01-01T12:00:00Z");
        var duplicateNotice2 = CreateNotice("Duplicate Notice", "https://platform.example/same", "2024-01-01T12:00:00Z");
        var uniqueNotice = CreateNotice("Unique Notice", "https://platform.example/unique");

        var collector1 = CreateMockCollector("platform1", BiddingCollectionResult.Success(
            "test-1", [duplicateNotice1, uniqueNotice]));
        var collector2 = CreateMockCollector("platform2", BiddingCollectionResult.Success(
            "test-2", [duplicateNotice2]));

        var composite = new CompositeBiddingNoticeCollector(
            [collector1, collector2],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-6");

        // Act
        var result = await composite.CollectAsync(request);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Succeeded, result.Status);
        Assert.Equal(2, result.Notices.Count); // Duplicate removed
        Assert.Contains(result.Notices, n => n.Title == "Unique Notice");
        Assert.Contains(result.Notices, n => n.Title == "Duplicate Notice");
    }

    [Fact]
    public async Task CollectAsync_respects_cancellation_token()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var collector1 = CreateMockCollector("platform1", BiddingCollectionResult.Cancelled("test-1"));

        var composite = new CompositeBiddingNoticeCollector(
            [collector1],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-7");

        // Act
        var result = await composite.CollectAsync(request, cts.Token);

        // Assert
        Assert.Equal(BiddingCollectionStatus.Cancelled, result.Status);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_calls_all_collectors_in_parallel()
    {
        // Arrange
        var delayCollector1 = new DelayingMockCollector("platform1", TimeSpan.FromMilliseconds(100));
        var delayCollector2 = new DelayingMockCollector("platform2", TimeSpan.FromMilliseconds(100));

        var composite = new CompositeBiddingNoticeCollector(
            [delayCollector1, delayCollector2],
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        var request = CreateRequest("composite-8");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        await composite.CollectAsync(request);

        stopwatch.Stop();

        // Assert: If parallel, should take ~100ms; if sequential, would take ~200ms
        Assert.True(stopwatch.ElapsedMilliseconds < 180,
            $"Expected parallel execution (~100ms), but took {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void SourcePlatform_returns_composite()
    {
        // Arrange
        var composite = new CompositeBiddingNoticeCollector(
            Array.Empty<IBiddingNoticeCollector>(),
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        // Assert
        Assert.Equal("composite", composite.SourcePlatform);
    }

    [Fact]
    public void IsConfigured_returns_true()
    {
        // Arrange
        var composite = new CompositeBiddingNoticeCollector(
            Array.Empty<IBiddingNoticeCollector>(),
            NullLogger<CompositeBiddingNoticeCollector>.Instance);

        // Assert
        Assert.True(composite.IsConfigured);
    }

    private static BiddingNotice CreateNotice(string title, string url, string? publishedAt = null)
    {
        var timestamp = publishedAt != null
            ? DateTimeOffset.Parse(publishedAt)
            : DateTimeOffset.UtcNow;

        var fingerprint = BiddingNoticeFingerprint.Compute("test-platform", url, title);

        return new BiddingNotice
        {
            Title = title,
            Publisher = "Test Publisher",
            PublishedAt = timestamp,
            NoticeUrl = url,
            SourcePlatform = "test-platform",
            Fingerprint = fingerprint
        };
    }

    private static BiddingCollectionRequest CreateRequest(string collectionId)
    {
        return new BiddingCollectionRequest
        {
            CollectionId = collectionId,
            Keywords = ["test"],
            MaxResults = 100
        };
    }

    private static IBiddingNoticeCollector CreateMockCollector(string platformId, BiddingCollectionResult result)
    {
        return new MockCollector(platformId, result);
    }

    private sealed class MockCollector : IBiddingNoticeCollector
    {
        private readonly BiddingCollectionResult _result;

        public MockCollector(string platformId, BiddingCollectionResult result)
        {
            SourcePlatform = platformId;
            _result = result;
        }

        public string SourcePlatform { get; }

        public bool IsConfigured => true;

        public Task<BiddingCollectionResult> CollectAsync(
            BiddingCollectionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_result);
        }
    }

    private sealed class DelayingMockCollector : IBiddingNoticeCollector
    {
        private readonly TimeSpan _delay;

        public DelayingMockCollector(string platformId, TimeSpan delay)
        {
            SourcePlatform = platformId;
            _delay = delay;
        }

        public string SourcePlatform { get; }

        public bool IsConfigured => true;

        public async Task<BiddingCollectionResult> CollectAsync(
            BiddingCollectionRequest request,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(_delay, cancellationToken);
            return BiddingCollectionResult.Success(request.CollectionId, Array.Empty<BiddingNotice>());
        }
    }
}
