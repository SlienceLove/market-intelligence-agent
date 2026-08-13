using System.Globalization;
using System.Text.RegularExpressions;
using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Mock RSS parser for testing. Parses simple RSS 2.0 feed format.
/// NOT for production use—real parsers go in separate implementations.
/// </summary>
public sealed class MockRssPlatformParser : IPlatformParser
{
    public string PlatformId => "mock-rss";

    public Uri BuildSearchUri(BiddingCollectionRequest request)
    {
        // Mock: return a publicly-resolvable URL so the SSRF guard passes in tests.
        // The stub HttpClient in tests intercepts the request regardless of host.
        return new Uri("https://example.com/rss");
    }

    public Task<BiddingNotice[]> ParseAsync(
        string content,
        BiddingCollectionRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Task.FromResult(Array.Empty<BiddingNotice>());
        }

        var notices = new List<BiddingNotice>();

        // Extract <item> blocks using simple regex
        var itemPattern = new Regex(@"<item>(.*?)</item>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var itemMatches = itemPattern.Matches(content);

        foreach (Match itemMatch in itemMatches)
        {
            var itemContent = itemMatch.Groups[1].Value;

            var title = ExtractTag(itemContent, "title");
            var link = ExtractTag(itemContent, "link");
            var pubDate = ExtractTag(itemContent, "pubDate");
            var publisher = ExtractTag(itemContent, "publisher") ?? "Unknown Publisher";

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link))
            {
                continue; // Skip invalid items
            }

            DateTimeOffset publishedAt;
            if (!string.IsNullOrWhiteSpace(pubDate))
            {
                // Try RFC 1123 format first, then ISO 8601
                if (!DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out publishedAt))
                {
                    publishedAt = DateTimeOffset.UtcNow;
                }
            }
            else
            {
                publishedAt = DateTimeOffset.UtcNow;
            }

            // Generate fingerprint from platform, link, and title
            var fingerprint = BiddingNoticeFingerprint.Compute(PlatformId, link, title);

            var notice = new BiddingNotice
            {
                Title = title,
                Publisher = publisher,
                PublishedAt = publishedAt,
                NoticeUrl = link,
                SourcePlatform = PlatformId,
                Fingerprint = fingerprint
            };

            notices.Add(notice);
        }

        return Task.FromResult(notices.ToArray());
    }

    private static string? ExtractTag(string content, string tagName)
    {
        var pattern = $@"<{tagName}>(.*?)</{tagName}>";
        var match = Regex.Match(content, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }
}
