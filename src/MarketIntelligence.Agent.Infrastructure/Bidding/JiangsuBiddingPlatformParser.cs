using System.Globalization;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Parses the public structured notice feed exposed by the Jiangsu Bidding and
/// Tendering Public Service Platform. The parser only retains fields allowed by
/// the bidding compliance boundary and never requests notice detail pages.
/// </summary>
public sealed class JiangsuBiddingPlatformParser : IPlatformParser
{
    private const string SearchEndpoint =
        "https://api.jszbtb.com/DataGatewayApi/PublishBulletins";
    private const string PlatformHome = "https://www.jszbtb.com/";
    private static readonly TimeSpan PlatformMaximumWindow = TimeSpan.FromDays(90);
    private static readonly TimeSpan ChinaStandardTimeOffset = TimeSpan.FromHours(8);

    private readonly TimeProvider _timeProvider;

    public JiangsuBiddingPlatformParser()
        : this(TimeProvider.System)
    {
    }

    internal JiangsuBiddingPlatformParser(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public string PlatformId => "jszbtb.com";

    public Uri BuildSearchUri(BiddingCollectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var to = request.ToDate ?? _timeProvider.GetUtcNow();
        var from = request.FromDate ?? to.Subtract(TimeSpan.FromDays(7));
        if (to - from > PlatformMaximumWindow)
        {
            from = to.Subtract(PlatformMaximumWindow);
        }

        // The public endpoint accepts one fuzzy-search phrase. Preserve all
        // requested terms in their caller-provided order rather than silently
        // dropping all but the first.
        var keyword = string.Join(' ', request.Keywords ?? []).Trim();
        var pageSize = Math.Clamp(request.MaxResults, 1, BiddingContractLimits.MaxResultsCeiling);

        var query = new Dictionary<string, string>
        {
            ["bulletinType"] = "1",
            ["industryCode"] = string.Empty,
            ["regionCode"] = string.Empty,
            ["startTime"] = FormatPlatformTime(from),
            ["endTime"] = FormatPlatformTime(to),
            ["keyword"] = keyword,
            ["currentPage"] = "1",
            ["pageSize"] = pageSize.ToString(CultureInfo.InvariantCulture)
        };

        var queryString = string.Join('&', query.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

        return new Uri($"{SearchEndpoint}?{queryString}");
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

        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (!root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
        {
            throw new FormatException("The Jiangsu platform returned an unsuccessful response.");
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("data", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("The Jiangsu platform response is missing its notice list.");
        }

        var notices = new List<BiddingNotice>();
        foreach (var item in items.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var title = GetString(item, "bulletinName");
            var bulletinId = GetString(item, "bulletinID");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(bulletinId))
            {
                continue;
            }

            var publisher = FirstNonEmpty(
                GetString(item, "projectCompany"),
                GetString(item, "medium"),
                GetString(item, "bulletinSourceName"),
                "江苏省招标投标公共服务平台");
            var publishedAt = ParsePublishedAt(GetString(item, "noticeSendTime"));
            var noticeUrl = BuildNoticeUrl(
                bulletinId,
                GetString(item, "bulletinType") ?? "1",
                GetString(item, "tenderProjectCode"));

            var notice = new BiddingNotice
            {
                Title = title.Trim(),
                Publisher = publisher,
                PublishedAt = publishedAt,
                Region = NullIfWhiteSpace(GetString(item, "regionName")),
                Industry = NullIfWhiteSpace(GetString(item, "industryName")),
                NoticeUrl = noticeUrl,
                SourcePlatform = PlatformId,
                Fingerprint = BiddingNoticeFingerprint.Compute(PlatformId, noticeUrl, title)
            };

            // An unexpected personal-data field or malformed value is a parser
            // boundary violation. Do not let it enter the aggregate result.
            if (notice.Validate() is null)
            {
                notices.Add(notice);
            }
        }

        return Task.FromResult(notices.ToArray());
    }

    private static string FormatPlatformTime(DateTimeOffset value) =>
        value.ToOffset(ChinaStandardTimeOffset)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParsePublishedAt(string? value)
    {
        if (DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return new DateTimeOffset(parsed, ChinaStandardTimeOffset);
        }

        throw new FormatException("A Jiangsu notice has an invalid publication time.");
    }

    private static string BuildNoticeUrl(
        string bulletinId,
        string bulletinType,
        string? tenderProjectCode)
    {
        var route = $"#/bulletinDetails/{Uri.EscapeDataString("招标公告")}/{Uri.EscapeDataString(bulletinId)}";
        var query = $"bulletinType={Uri.EscapeDataString(bulletinType)}";
        if (!string.IsNullOrWhiteSpace(tenderProjectCode))
        {
            query += $"&tenderProjectCode={Uri.EscapeDataString(tenderProjectCode)}";
        }

        return $"{PlatformHome}{route}?{query}";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!.Trim();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
