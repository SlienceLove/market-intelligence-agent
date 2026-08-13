using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Development-only fixture collector that returns hardcoded bidding notices.
/// Bypasses HTTP, robots.txt, and rate limiting entirely.
/// Register only when ASPNETCORE_ENVIRONMENT=Development.
/// </summary>
/// <remarks>
/// This collector exists solely for local demos and integration smoke tests.
/// It must never be registered in Production or Staging environments.
/// </remarks>
public sealed class DemoFixtureBiddingNoticeCollector : IBiddingNoticeCollector
{
    public string CollectorId => "demo-fixture";
    public string SourcePlatform => "demo-fixture";
    public bool IsConfigured => true;

    public Task<BiddingCollectionResult> CollectAsync(
        BiddingCollectionRequest request,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        var notices = new[]
        {
            new BiddingNotice
            {
                Title = "某市政务云平台建设项目公开招标公告",
                Publisher = "某市政府采购中心",
                PublishedAt = now.AddDays(-1),
                Region = "北京",
                Industry = "信息技术",
                AmountRange = "500-1000万元",
                NoticeUrl = "https://www.ccgp.gov.cn/cggg/dfgg/zbgg/demo-001.htm",
                SourcePlatform = "demo-fixture",
                Fingerprint = BiddingNoticeFingerprint.Compute(
                    "demo-fixture",
                    "https://www.ccgp.gov.cn/cggg/dfgg/zbgg/demo-001.htm",
                    "某市政务云平台建设项目公开招标公告")
            },
            new BiddingNotice
            {
                Title = "企业数字化转型软件采购项目竞争性谈判公告",
                Publisher = "某省国有资产监督管理委员会",
                PublishedAt = now.AddDays(-1),
                Region = "上海",
                Industry = "软件服务",
                AmountRange = "100-300万元",
                NoticeUrl = "https://www.ccgp.gov.cn/cggg/dfgg/cftgg/demo-002.htm",
                SourcePlatform = "demo-fixture",
                Fingerprint = BiddingNoticeFingerprint.Compute(
                    "demo-fixture",
                    "https://www.ccgp.gov.cn/cggg/dfgg/cftgg/demo-002.htm",
                    "企业数字化转型软件采购项目竞争性谈判公告")
            },
            new BiddingNotice
            {
                Title = "智慧城市大数据平台运维服务采购项目招标公告",
                Publisher = "某区大数据局",
                PublishedAt = now.AddHours(-6),
                Region = "广东",
                Industry = "大数据",
                AmountRange = "50-100万元",
                NoticeUrl = "https://www.ccgp.gov.cn/cggg/dfgg/zbgg/demo-003.htm",
                SourcePlatform = "demo-fixture",
                Fingerprint = BiddingNoticeFingerprint.Compute(
                    "demo-fixture",
                    "https://www.ccgp.gov.cn/cggg/dfgg/zbgg/demo-003.htm",
                    "智慧城市大数据平台运维服务采购项目招标公告")
            }
        };

        // Filter by keywords if specified in the request
        var filtered = request.Keywords.Count > 0
            ? notices.Where(n => request.Keywords.Any(k =>
                n.Title.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                (n.Industry?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false)))
              .ToArray()
            : notices;

        return Task.FromResult(BiddingCollectionResult.Success(CollectorId, filtered.ToList()));
    }
}
