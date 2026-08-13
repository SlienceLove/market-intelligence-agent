using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Collects bidding notices from a single HTTP-based platform.
/// Integrates robots.txt checking, rate limiting, HTTP fetching, and platform parsing.
/// </summary>
public sealed class HttpBiddingNoticeCollector : IBiddingNoticeCollector
{
    private readonly IPlatformParser _parser;
    private readonly RobotsTxtCache _robotsCache;
    private readonly IBiddingRateLimiter _rateLimiter;
    private readonly HttpClient _httpClient;
    private readonly ILogger<HttpBiddingNoticeCollector> _logger;

    public HttpBiddingNoticeCollector(
        IPlatformParser parser,
        RobotsTxtCache robotsCache,
        IBiddingRateLimiter rateLimiter,
        HttpClient httpClient,
        ILogger<HttpBiddingNoticeCollector> logger)
    {
        _parser = parser ?? throw new ArgumentNullException(nameof(parser));
        _robotsCache = robotsCache ?? throw new ArgumentNullException(nameof(robotsCache));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string SourcePlatform => _parser.PlatformId;

    public bool IsConfigured => true;

    public async Task<BiddingCollectionResult> CollectAsync(
        BiddingCollectionRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Build platform URL from request
            var uri = _parser.BuildSearchUri(request);

            // 1a. SSRF guard: reject private/internal endpoints before any outbound request
            if (!SsrfGuard.IsCollectionUrlSafe(uri))
            {
                _logger.LogWarning(
                    "SSRF guard blocked collection from {Uri} for platform {Platform}",
                    uri,
                    _parser.PlatformId);

                return BiddingCollectionResult.Failed(
                    request.CollectionId,
                    "bidding_source_not_allowed",
                    "Collection URI blocked by SSRF guard",
                    request.CorrelationId);
            }

            _logger.LogInformation(
                "Starting HTTP collection for platform {Platform} at {Uri}",
                _parser.PlatformId,
                uri);

            // 2. Check robots.txt
            var allowed = await _robotsCache.IsAllowedAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                _logger.LogWarning(
                    "robots.txt disallows access to {Uri} for platform {Platform}",
                    uri,
                    _parser.PlatformId);

                return BiddingCollectionResult.Failed(
                    request.CollectionId,
                    "robots_disallowed",
                    "robots.txt disallows access",
                    request.CorrelationId);
            }

            // 3. Wait for rate limiter
            await _rateLimiter.WaitAsync(_parser.PlatformId, cancellationToken).ConfigureAwait(false);

            // 4. Fetch HTTP content — HttpClient.Timeout (30 s) covers the per-request deadline
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "HTTP request to {Uri} timed out for platform {Platform}",
                    uri,
                    _parser.PlatformId);

                return BiddingCollectionResult.Failed(
                    request.CollectionId,
                    "timeout",
                    "HTTP request timed out",
                    request.CorrelationId);
            }

            using (response)
            {
                // 5. Handle HTTP status codes
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogInformation(
                        "Platform {Platform} returned 404 for {Uri}",
                        _parser.PlatformId,
                        uri);

                    return BiddingCollectionResult.Success(
                        request.CollectionId,
                        Array.Empty<BiddingNotice>(),
                        request.CorrelationId);
                }

                if (response.StatusCode == (System.Net.HttpStatusCode)429)
                {
                    _logger.LogWarning(
                        "Platform {Platform} rate limited request to {Uri}",
                        _parser.PlatformId,
                        uri);

                    return BiddingCollectionResult.Failed(
                        request.CollectionId,
                        "rate_limited",
                        "Platform rate limit exceeded",
                        request.CorrelationId);
                }

                if ((int)response.StatusCode >= 500)
                {
                    _logger.LogWarning(
                        "Platform {Platform} returned server error {StatusCode} for {Uri}",
                        _parser.PlatformId,
                        (int)response.StatusCode,
                        uri);

                    return BiddingCollectionResult.Failed(
                        request.CollectionId,
                        "provider_unavailable",
                        $"Platform returned {(int)response.StatusCode}",
                        request.CorrelationId);
                }

                if ((int)response.StatusCode >= 400)
                {
                    _logger.LogWarning(
                        "Platform {Platform} returned client error {StatusCode} for {Uri}",
                        _parser.PlatformId,
                        (int)response.StatusCode,
                        uri);

                    return BiddingCollectionResult.Failed(
                        request.CollectionId,
                        "invalid_request",
                        $"Platform returned {(int)response.StatusCode}",
                        request.CorrelationId);
                }

                response.EnsureSuccessStatusCode();

                // 6. Read and parse content
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                var notices = await _parser.ParseAsync(content, request, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "Successfully collected {Count} notices from platform {Platform}",
                    notices.Length,
                    _parser.PlatformId);

                return BiddingCollectionResult.Success(
                    request.CollectionId,
                    notices,
                    request.CorrelationId,
                    request.MaxResults);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Collection cancelled for platform {Platform}",
                _parser.PlatformId);

            return BiddingCollectionResult.Cancelled(
                request.CollectionId,
                request.CorrelationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unexpected error collecting from platform {Platform}: {Message}",
                _parser.PlatformId,
                ex.Message);

            return BiddingCollectionResult.Failed(
                request.CollectionId,
                "internal_error",
                "Collector encountered an unexpected error",
                request.CorrelationId);
        }
    }
}
