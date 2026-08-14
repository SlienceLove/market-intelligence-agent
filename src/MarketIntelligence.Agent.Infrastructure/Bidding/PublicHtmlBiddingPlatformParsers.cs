using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Parses the public latest-notice links rendered on the National Public
/// Resources Trading Platform home page. Search pages are deliberately not used
/// because their interactive flow can require a verification code.
/// </summary>
public sealed class NationalPublicResourcePlatformParser : IPlatformParser
{
    private static readonly Regex NoticeLinkPattern = new(
        "<a\\s+[^>]*href=[\\\"'](?<href>/information/deal/[^\\\"']+\\.html)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

    public string PlatformId => "ggzy.gov.cn";

    public Uri BuildSearchUri(BiddingCollectionRequest request) =>
        new("https://www.ggzy.gov.cn/");

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
        foreach (Match match in NoticeLinkPattern.Matches(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = PublicHtmlNoticeParser.NormalizeText(match.Groups["title"].Value);
            if (!PublicHtmlNoticeParser.MatchesKeywords(title, request.Keywords))
            {
                continue;
            }

            var relativeUrl = WebUtility.HtmlDecode(match.Groups["href"].Value);
            var noticeUrl = new Uri(new Uri("https://www.ggzy.gov.cn/"), relativeUrl).AbsoluteUri;
            if (!PublicHtmlNoticeParser.TryParseDateFromPath(relativeUrl, out var publishedAt))
            {
                continue;
            }
            if (!PublicHtmlNoticeParser.IsWithinRequestedWindow(publishedAt, request))
            {
                continue;
            }

            var notice = PublicHtmlNoticeParser.CreateNotice(
                title,
                "全国公共资源交易平台",
                publishedAt,
                noticeUrl,
                PlatformId);
            if (notice.Validate() is null)
            {
                notices.Add(notice);
            }
        }

        return Task.FromResult(notices.ToArray());
    }
}

/// <summary>
/// Parses the public latest procurement notices on the Jiangsu Government
/// Procurement home page. It does not call the advanced search endpoint because
/// that endpoint requires a verification code.
/// </summary>
public sealed class JiangsuGovernmentProcurementParser : IPlatformParser
{
    private static readonly Regex RowPattern = new(
        "<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex NoticeLinkPattern = new(
        "<a\\s+[^>]*href=[\\\"'](?<href>/jiangsu/js_cggg/details\\.html\\?[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex PublishedDatePattern = new(
        @"\b(?<date>20\d{2}-\d{2}-\d{2})\b",
        RegexOptions.CultureInvariant);

    public string PlatformId => "ccgp-jiangsu.gov.cn";

    public Uri BuildSearchUri(BiddingCollectionRequest request) =>
        new("http://www.ccgp-jiangsu.gov.cn/");

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
        foreach (Match rowMatch in RowPattern.Matches(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rowMatch.Groups["row"].Value;
            var linkMatch = NoticeLinkPattern.Match(row);
            var dateMatch = PublishedDatePattern.Match(row);
            if (!linkMatch.Success || !dateMatch.Success)
            {
                continue;
            }

            var title = PublicHtmlNoticeParser.NormalizeText(linkMatch.Groups["title"].Value);
            if (!PublicHtmlNoticeParser.MatchesKeywords(title, request.Keywords))
            {
                continue;
            }

            if (!DateTime.TryParseExact(
                    dateMatch.Groups["date"].Value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date))
            {
                continue;
            }

            var publishedAt = new DateTimeOffset(date, TimeSpan.FromHours(8));
            if (!PublicHtmlNoticeParser.IsWithinRequestedWindow(publishedAt, request))
            {
                continue;
            }

            var relativeUrl = WebUtility.HtmlDecode(linkMatch.Groups["href"].Value);
            var noticeUrl = new Uri(
                new Uri("http://www.ccgp-jiangsu.gov.cn/"),
                relativeUrl).AbsoluteUri;
            var notice = PublicHtmlNoticeParser.CreateNotice(
                title,
                "江苏政府采购网",
                publishedAt,
                noticeUrl,
                PlatformId,
                region: "江苏省");
            if (notice.Validate() is null)
            {
                notices.Add(notice);
            }
        }

        return Task.FromResult(notices.ToArray());
    }
}

internal static class PublicHtmlNoticeParser
{
    private static readonly Regex HtmlTagPattern = new(
        "<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex WhitespacePattern = new(
        @"\s+",
        RegexOptions.CultureInvariant);
    private static readonly Regex DatePathPattern = new(
        @"/(?<date>20\d{6})/",
        RegexOptions.CultureInvariant);

    public static bool MatchesKeywords(string title, IReadOnlyList<string>? keywords) =>
        keywords is { Count: > 0 } && keywords.Any(keyword =>
            !string.IsNullOrWhiteSpace(keyword) &&
            title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string NormalizeText(string html)
    {
        var withoutTags = HtmlTagPattern.Replace(html, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespacePattern.Replace(decoded, " ").Trim();
    }

    public static bool TryParseDateFromPath(string path, out DateTimeOffset publishedAt)
    {
        var match = DatePathPattern.Match(path);
        if (match.Success && DateTime.TryParseExact(
                match.Groups["date"].Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            publishedAt = new DateTimeOffset(date, TimeSpan.FromHours(8));
            return true;
        }

        publishedAt = default;
        return false;
    }

    public static bool IsWithinRequestedWindow(
        DateTimeOffset publishedAt,
        BiddingCollectionRequest request) =>
        (request.FromDate is null || publishedAt >= request.FromDate) &&
        (request.ToDate is null || publishedAt <= request.ToDate);

    public static BiddingNotice CreateNotice(
        string title,
        string publisher,
        DateTimeOffset publishedAt,
        string noticeUrl,
        string sourcePlatform,
        string? region = null) =>
        new()
        {
            Title = title,
            Publisher = publisher,
            PublishedAt = publishedAt,
            Region = region,
            NoticeUrl = noticeUrl,
            SourcePlatform = sourcePlatform,
            Fingerprint = BiddingNoticeFingerprint.Compute(sourcePlatform, noticeUrl, title)
        };
}
