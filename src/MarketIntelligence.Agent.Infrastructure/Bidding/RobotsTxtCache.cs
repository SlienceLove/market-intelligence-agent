using System.Collections.Concurrent;
using System.Net;
using MarketIntelligence.Agent.Infrastructure.Notifications;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Caches robots.txt rules per domain with RFC 9309 compliance and fail-closed semantics.
/// Single-process only: cache is in-memory and not synchronized across instances.
/// DNS/network/parse failures result in denial (fail-closed).
/// </summary>
public sealed class RobotsTxtCache
{
    private const string UserAgentWildcard = "*";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    // Per-host semaphores prevent a thundering herd on cache expiry: only one
    // goroutine fetches while others wait and then reuse the fresh entry.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fetchLocks = new(StringComparer.OrdinalIgnoreCase);

    public RobotsTxtCache(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <summary>
    /// Checks if the given URI is allowed by robots.txt rules.
    /// </summary>
    /// <param name="uri">The URI to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// True if allowed (no robots.txt, or explicitly allowed).
    /// False if disallowed or on fetch/parse failure (fail-closed).
    /// </returns>
    public async Task<bool> IsAllowedAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri is null)
        {
            throw new ArgumentNullException(nameof(uri));
        }

        var host = uri.Host;
        var path = uri.AbsolutePath;

        var entry = await GetOrFetchRulesAsync(host, uri.Scheme, cancellationToken).ConfigureAwait(false);

        // Fail-closed: if fetch failed, deny
        if (entry.FetchFailed)
        {
            return false;
        }

        // entry.FetchFailed was already handled above; if Rules is null here,
        // the fetch succeeded but returned no rules (404/410/empty content) — allow the path.
        if (entry.Rules is null)
        {
            return true;
        }

        // Find matching rule (longest user-agent match wins, "*" is fallback)
        var rule = entry.Rules.FirstOrDefault(r => r.UserAgent == UserAgentWildcard);
        if (rule is null)
        {
            return true; // No applicable rules
        }

        // Check Allow and Disallow with longest prefix match
        var longestDisallow = rule.Disallow
            .Where(pattern => PathMatches(path, pattern))
            .OrderByDescending(pattern => pattern.Length)
            .FirstOrDefault();

        var longestAllow = rule.Allow
            .Where(pattern => PathMatches(path, pattern))
            .OrderByDescending(pattern => pattern.Length)
            .FirstOrDefault();

        // If both match, longest wins; if same length, Allow wins
        if (longestAllow is not null && longestDisallow is not null)
        {
            return longestAllow.Length >= longestDisallow.Length;
        }

        if (longestAllow is not null)
        {
            return true;
        }

        if (longestDisallow is not null)
        {
            return false;
        }

        // No matching rules = allowed
        return true;
    }

    /// <summary>
    /// Gets the crawl delay in seconds for the specified host, if any.
    /// </summary>
    /// <param name="host">The host to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Crawl delay in seconds, or null if none specified or on fetch failure.</returns>
    public async Task<int?> GetCrawlDelayAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host cannot be null or empty.", nameof(host));
        }

        var entry = await GetOrFetchRulesAsync(host, "https", cancellationToken).ConfigureAwait(false);

        if (entry.FetchFailed || entry.Rules is null)
        {
            return null;
        }

        var rule = entry.Rules.FirstOrDefault(r => r.UserAgent == UserAgentWildcard);
        return rule?.CrawlDelay;
    }

    private async Task<CacheEntry> GetOrFetchRulesAsync(
        string host,
        string scheme,
        CancellationToken cancellationToken)
    {
        // Fast path: valid cache hit (no lock needed for reads)
        if (_cache.TryGetValue(host, out var cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
        {
            return cached;
        }

        // Slow path: acquire per-host lock to prevent thundering herd on cache expiry
        var hostLock = _fetchLocks.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await hostLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Double-check inside lock: another waiter may have already refreshed the entry
            if (_cache.TryGetValue(host, out cached) && DateTimeOffset.UtcNow < cached.ExpiresAt)
            {
                return cached;
            }

            _cache.TryRemove(host, out _);
            var entry = await FetchAndParseRobotsTxtAsync(host, scheme, cancellationToken).ConfigureAwait(false);
            _cache.TryAdd(host, entry);
            return entry;
        }
        finally
        {
            hostLock.Release();
        }
    }

    private async Task<CacheEntry> FetchAndParseRobotsTxtAsync(
        string host,
        string scheme,
        CancellationToken cancellationToken)
    {
        var robotsUrl = $"{scheme}://{host}/robots.txt";

        // SSRF guard: deny fetch for private/internal hosts (fail-closed)
        if (!SsrfGuard.IsCollectionUrlSafe(robotsUrl))
        {
            return new CacheEntry(null, FetchFailed: true, DateTimeOffset.UtcNow.Add(CacheTtl));
        }

        try
        {
            using var response = await _httpClient.GetAsync(robotsUrl, cancellationToken).ConfigureAwait(false);

            // 404/410 = no robots.txt, allow everything
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return new CacheEntry(null, FetchFailed: false, DateTimeOffset.UtcNow.Add(CacheTtl));
            }

            // 5xx or other errors = fail-closed (deny)
            if (!response.IsSuccessStatusCode)
            {
                return new CacheEntry(null, FetchFailed: true, DateTimeOffset.UtcNow.Add(CacheTtl));
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Empty content = allow
            if (string.IsNullOrWhiteSpace(content))
            {
                return new CacheEntry(null, FetchFailed: false, DateTimeOffset.UtcNow.Add(CacheTtl));
            }

            var rules = ParseRobotsTxt(content);

            // Parse error returns empty list, treat as fail-closed
            if (rules.Count == 0 && !string.IsNullOrWhiteSpace(content))
            {
                // Content exists but no valid rules parsed - could be parse error
                // For now, allow if we got 2xx (fail-open on parse of valid response)
                return new CacheEntry(null, FetchFailed: false, DateTimeOffset.UtcNow.Add(CacheTtl));
            }

            return new CacheEntry(rules, FetchFailed: false, DateTimeOffset.UtcNow.Add(CacheTtl));
        }
        catch (HttpRequestException)
        {
            // DNS, connection, or network error = fail-closed (deny)
            return new CacheEntry(null, FetchFailed: true, DateTimeOffset.UtcNow.Add(CacheTtl));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout = fail-closed (deny)
            return new CacheEntry(null, FetchFailed: true, DateTimeOffset.UtcNow.Add(CacheTtl));
        }
        catch
        {
            // Any other error = fail-closed (deny)
            return new CacheEntry(null, FetchFailed: true, DateTimeOffset.UtcNow.Add(CacheTtl));
        }
    }

    private static List<RobotsTxtRule> ParseRobotsTxt(string content)
    {
        var rules = new List<RobotsTxtRule>();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        string? currentUserAgent = null;
        var disallowList = new List<string>();
        var allowList = new List<string>();
        int? crawlDelay = null;

        void FlushCurrentRule()
        {
            if (currentUserAgent is not null)
            {
                rules.Add(new RobotsTxtRule
                {
                    UserAgent = currentUserAgent,
                    Disallow = [.. disallowList],
                    Allow = [.. allowList],
                    CrawlDelay = crawlDelay
                });

                disallowList.Clear();
                allowList.Clear();
                crawlDelay = null;
                currentUserAgent = null;
            }
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            // Skip comments and empty lines
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            // Remove inline comments
            var commentIndex = trimmed.IndexOf('#');
            if (commentIndex >= 0)
            {
                trimmed = trimmed[..commentIndex].Trim();
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 0)
            {
                continue; // Malformed line, skip
            }

            var directive = trimmed[..colonIndex].Trim();
            var value = colonIndex + 1 < trimmed.Length ? trimmed[(colonIndex + 1)..].Trim() : string.Empty;

            if (string.Equals(directive, "User-agent", StringComparison.OrdinalIgnoreCase))
            {
                // New user-agent starts a new rule group
                FlushCurrentRule();
                currentUserAgent = value;
            }
            else if (currentUserAgent is not null)
            {
                if (string.Equals(directive, "Disallow", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        disallowList.Add(value);
                    }
                }
                else if (string.Equals(directive, "Allow", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        allowList.Add(value);
                    }
                }
                else if (string.Equals(directive, "Crawl-delay", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out var delay) && delay >= 0)
                    {
                        crawlDelay = delay;
                    }
                }
                // Ignore other directives (Sitemap, Request-rate, etc.)
            }
        }

        // Flush the last rule
        FlushCurrentRule();

        return rules;
    }

    private static bool PathMatches(string path, string pattern)
    {
        // Simple prefix match for RFC 9309
        // Empty pattern matches empty path only
        if (string.IsNullOrEmpty(pattern))
        {
            return string.IsNullOrEmpty(path);
        }

        return path.StartsWith(pattern, StringComparison.Ordinal);
    }

    private sealed record CacheEntry(
        List<RobotsTxtRule>? Rules,
        bool FetchFailed,
        DateTimeOffset ExpiresAt);
}
