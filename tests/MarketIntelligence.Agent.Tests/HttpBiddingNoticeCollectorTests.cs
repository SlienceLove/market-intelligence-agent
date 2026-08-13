using System.Net;
using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class HttpBiddingNoticeCollectorTests
{
    [Fact]
    public async Task CollectAsync_returns_success_with_parsed_notices_on_200_response()
    {
        var rssContent = """
            <?xml version="1.0"?>
            <rss version="2.0">
            <channel>
                <item>
                    <title>Test Bidding Notice</title>
                    <link>https://mock-platform.example/notice/1</link>
                    <pubDate>Mon, 01 Jan 2024 12:00:00 GMT</pubDate>
                    <publisher>Test Publisher</publisher>
                </item>
            </channel>
            </rss>
            """;

        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rssContent)
            }));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-1");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Succeeded, result.Status);
        Assert.Single(result.Notices);
        Assert.Equal("Test Bidding Notice", result.Notices[0].Title);
        Assert.Equal("Test Publisher", result.Notices[0].Publisher);
        Assert.Equal("https://mock-platform.example/notice/1", result.Notices[0].NoticeUrl);
        Assert.Equal("mock-rss", result.Notices[0].SourcePlatform);
    }

    [Fact]
    public async Task CollectAsync_returns_robots_disallowed_when_blocked_by_robots_txt()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /
            """;

        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss></rss>")
            }));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(robotsTxt)
            }));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-2");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("robots_disallowed", result.FailureCode);
        Assert.Empty(result.Notices);
        Assert.Equal(0, httpHandler.CallCount); // No HTTP request should be made
    }

    [Fact]
    public async Task CollectAsync_waits_for_rate_limiter_before_making_request()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss></rss>")
            }));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler, minimumIntervalSeconds: 1);
        var request1 = CreateRequest("test-collection-3a");
        var request2 = CreateRequest("test-collection-3b");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        await collector.CollectAsync(request1);
        await collector.CollectAsync(request2);

        stopwatch.Stop();

        // Should take at least 1 second due to rate limiting
        Assert.True(stopwatch.ElapsedMilliseconds >= 900,
            $"Expected at least 900ms delay, got {stopwatch.ElapsedMilliseconds}ms");
        Assert.Equal(2, httpHandler.CallCount);
    }

    [Fact]
    public async Task CollectAsync_returns_success_with_empty_notices_on_404()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-4");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Succeeded, result.Status);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_returns_rate_limited_on_429()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-5");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("rate_limited", result.FailureCode);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_returns_provider_unavailable_on_5xx()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-6");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("provider_unavailable", result.FailureCode);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_returns_timeout_on_slow_response()
    {
        var httpHandler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss></rss>")
            };
        });

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        // FIX 10: inner CTS removed; HttpClient.Timeout now drives the deadline
        var collector = CreateCollector(httpHandler, robotsHandler,
            httpClientTimeout: TimeSpan.FromMilliseconds(50));
        var request = CreateRequest("test-collection-7");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("timeout", result.FailureCode);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_returns_success_with_empty_notices_when_parser_finds_nothing()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss><channel></channel></rss>")
            }));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-8");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Succeeded, result.Status);
        Assert.Empty(result.Notices);
    }

    [Fact]
    public async Task CollectAsync_returns_cancelled_when_cancellation_requested()
    {
        var httpHandler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<rss></rss>")
            };
        });

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-9");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var result = await collector.CollectAsync(request, cts.Token);

        Assert.Equal(BiddingCollectionStatus.Cancelled, result.Status);
    }

    [Fact]
    public void SourcePlatform_returns_parser_platform_id()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);

        Assert.Equal("mock-rss", collector.SourcePlatform);
    }

    [Fact]
    public async Task CollectAsync_returns_invalid_request_on_4xx_client_errors()
    {
        var httpHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)));

        var robotsHandler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        var collector = CreateCollector(httpHandler, robotsHandler);
        var request = CreateRequest("test-collection-11");

        var result = await collector.CollectAsync(request);

        Assert.Equal(BiddingCollectionStatus.Failed, result.Status);
        Assert.Equal("invalid_request", result.FailureCode);
        Assert.Empty(result.Notices);
    }

    private static HttpBiddingNoticeCollector CreateCollector(
        HttpMessageHandler httpHandler,
        HttpMessageHandler robotsHandler,
        int minimumIntervalSeconds = 0,
        TimeSpan? httpClientTimeout = null)
    {
        var parser = new MockRssPlatformParser();
        var robotsCache = new RobotsTxtCache(new HttpClient(robotsHandler));

        var options = Options.Create(new BiddingOptions
        {
            Collector = new CollectorSettings
            {
                MinimumIntervalSeconds = minimumIntervalSeconds,
                GlobalQpsLimit = 100
            }
        });

        var rateLimiter = new BiddingRateLimiter(options);
        var httpClient = new HttpClient(httpHandler);
        if (httpClientTimeout.HasValue)
        {
            httpClient.Timeout = httpClientTimeout.Value;
        }

        var logger = NullLogger<HttpBiddingNoticeCollector>.Instance;

        return new HttpBiddingNoticeCollector(
            parser,
            robotsCache,
            rateLimiter,
            httpClient,
            logger);
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

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await responder(request, cancellationToken);
        }
    }
}
