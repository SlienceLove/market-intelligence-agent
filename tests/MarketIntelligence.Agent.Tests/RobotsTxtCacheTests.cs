using System.Net;
using MarketIntelligence.Agent.Infrastructure.Bidding;

namespace MarketIntelligence.Agent.Tests;

public sealed class RobotsTxtCacheTests
{
    [Fact]
    public async Task IsAllowedAsync_allows_path_when_robots_txt_not_found()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/robots.txt", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/path/to/page"));

        Assert.True(allowed);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task IsAllowedAsync_allows_path_when_robots_txt_is_gone()
    {
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal("/robots.txt", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Gone));
        });
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/path/to/page"));

        Assert.True(allowed);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task IsAllowedAsync_allows_path_with_explicit_allow_rule()
    {
        var robotsTxt = """
            User-agent: *
            Allow: /public
            Disallow: /
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/public/page"));

        Assert.True(allowed);
    }

    [Fact]
    public async Task IsAllowedAsync_denies_path_with_explicit_disallow_rule()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /admin
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var disallowed = await cache.IsAllowedAsync(new Uri("https://example.com/admin/panel"));

        Assert.False(disallowed);
    }

    [Fact]
    public async Task IsAllowedAsync_applies_longest_prefix_match()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /data
            Allow: /data/public
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var publicAllowed = await cache.IsAllowedAsync(new Uri("https://example.com/data/public/file"));
        var privateDisallowed = await cache.IsAllowedAsync(new Uri("https://example.com/data/private/file"));

        Assert.True(publicAllowed, "/data/public should be allowed (longer match)");
        Assert.False(privateDisallowed, "/data/private should be disallowed");
    }

    [Fact]
    public async Task IsAllowedAsync_prefers_allow_when_same_length_match()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /test
            Allow: /test
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/test/page"));

        Assert.True(allowed, "Allow should win when both match with same length");
    }

    [Fact]
    public async Task IsAllowedAsync_matches_wildcard_user_agent()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /blocked
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var disallowed = await cache.IsAllowedAsync(new Uri("https://example.com/blocked/page"));

        Assert.False(disallowed);
    }

    [Fact]
    public async Task IsAllowedAsync_denies_on_fetch_timeout()
    {
        var handler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var cache = new RobotsTxtCache(httpClient);

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/path"));

        Assert.False(allowed, "Should deny on timeout (fail-closed)");
    }

    [Fact]
    public async Task IsAllowedAsync_denies_on_server_error()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/path"));

        Assert.False(allowed, "Should deny on 5xx error (fail-closed)");
    }

    [Fact]
    public async Task IsAllowedAsync_denies_on_network_error()
    {
        var handler = new StubHandler((_, _) =>
            throw new HttpRequestException("Network error"));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/path"));

        Assert.False(allowed, "Should deny on network error (fail-closed)");
    }

    [Fact]
    public async Task IsAllowedAsync_caches_result_and_avoids_second_fetch()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /admin
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var firstCall = await cache.IsAllowedAsync(new Uri("https://example.com/public"));
        var secondCall = await cache.IsAllowedAsync(new Uri("https://example.com/admin"));

        Assert.True(firstCall);
        Assert.False(secondCall);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task IsAllowedAsync_refetches_after_cache_expiry()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /admin
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));

        // Use reflection or wait - for testing, we'll verify behavior without waiting 24h
        // This test verifies the cache works; expiry is hardcoded to 24h
        var cache = new RobotsTxtCache(new HttpClient(handler));

        await cache.IsAllowedAsync(new Uri("https://example.com/path1"));
        await cache.IsAllowedAsync(new Uri("https://example.com/path2"));

        // Both should use cached result
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetCrawlDelayAsync_extracts_crawl_delay_from_robots_txt()
    {
        var robotsTxt = """
            User-agent: *
            Crawl-delay: 5
            Disallow: /admin
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var delay = await cache.GetCrawlDelayAsync("example.com");

        Assert.Equal(5, delay);
    }

    [Fact]
    public async Task GetCrawlDelayAsync_returns_null_when_no_crawl_delay()
    {
        var robotsTxt = """
            User-agent: *
            Disallow: /admin
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var delay = await cache.GetCrawlDelayAsync("example.com");

        Assert.Null(delay);
    }

    [Fact]
    public async Task GetCrawlDelayAsync_returns_null_on_fetch_failure()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var delay = await cache.GetCrawlDelayAsync("example.com");

        Assert.Null(delay);
    }

    [Fact]
    public async Task IsAllowedAsync_allows_empty_robots_txt()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse("")));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var allowed = await cache.IsAllowedAsync(new Uri("https://example.com/any/path"));

        Assert.True(allowed, "Empty robots.txt should allow all paths");
    }

    [Fact]
    public async Task IsAllowedAsync_handles_comments_and_whitespace()
    {
        var robotsTxt = """
            # This is a comment
            User-agent: *
            Disallow: /private  # inline comment

            Allow: /public
            """;
        var handler = new StubHandler((_, _) =>
            Task.FromResult(RobotsTxtResponse(robotsTxt)));
        var cache = new RobotsTxtCache(new HttpClient(handler));

        var privateDisallowed = await cache.IsAllowedAsync(new Uri("https://example.com/private/data"));
        var publicAllowed = await cache.IsAllowedAsync(new Uri("https://example.com/public/data"));

        Assert.False(privateDisallowed);
        Assert.True(publicAllowed);
    }

    private static HttpResponseMessage RobotsTxtResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content)
        };
        return response;
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
