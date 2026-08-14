using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Low-volume live checks for the three explicitly supported public platforms.
/// Each test performs one robots request and one collection request, uses the
/// project User-Agent, and never fetches a notice detail page.
/// </summary>
public sealed class RealBiddingPlatformSmokeTests
{
    [RequiresRealBiddingPlatformsFact]
    public async Task Jiangsu_bidding_structured_feed_returns_a_public_notice()
    {
        var request = new BiddingCollectionRequest
        {
            CollectionId = "live-jszbtb",
            Keywords = ["招租"],
            FromDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
            ToDate = new DateTimeOffset(2025, 3, 31, 23, 59, 59, TimeSpan.FromHours(8)),
            MaxResults = 1
        };

        await AssertLiveResultAsync(new JiangsuBiddingPlatformParser(), request);
    }

    [RequiresRealBiddingPlatformsFact]
    public async Task National_public_resource_home_page_returns_a_public_notice()
    {
        await AssertLiveResultAsync(
            new NationalPublicResourcePlatformParser(),
            CreateCurrentRequest("公告", "live-ggzy"));
    }

    [RequiresRealBiddingPlatformsFact]
    public async Task Jiangsu_government_procurement_home_page_returns_a_public_notice()
    {
        await AssertLiveResultAsync(
            new JiangsuGovernmentProcurementParser(),
            CreateCurrentRequest("采购", "live-ccgp-jiangsu"));
    }

    private static BiddingCollectionRequest CreateCurrentRequest(
        string keyword,
        string collectionId) => new()
        {
            CollectionId = collectionId,
            Keywords = [keyword],
            MaxResults = 3
        };

    private static async Task AssertLiveResultAsync(
        IPlatformParser parser,
        BiddingCollectionRequest request)
    {
        using var collectionClient = CreateClient(TimeSpan.FromSeconds(30));
        using var robotsClient = CreateClient(TimeSpan.FromSeconds(10));
        var collector = new HttpBiddingNoticeCollector(
            parser,
            new RobotsTxtCache(robotsClient),
            new BiddingRateLimiter(Options.Create(new BiddingOptions
            {
                Collector = new CollectorSettings
                {
                    MinimumIntervalSeconds = 2,
                    GlobalQpsLimit = 5
                }
            })),
            collectionClient,
            NullLogger<HttpBiddingNoticeCollector>.Instance);

        var result = await collector.CollectAsync(request);

        Assert.True(result.Succeeded, $"{parser.PlatformId}: {result.FailureCode}");
        Assert.NotEmpty(result.Notices);
        Assert.All(result.Notices, notice => Assert.Null(notice.Validate()));
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MarketIntelligenceAgent/1.0 (+https://github.com/SlienceLove/market-intelligence-agent)");
        return client;
    }
}
