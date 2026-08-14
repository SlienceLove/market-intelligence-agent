using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Bidding;

namespace MarketIntelligence.Agent.Tests;

public sealed class JiangsuBiddingPlatformParserTests
{
    [Fact]
    public void BuildSearchUri_uses_public_api_and_bounded_request_values()
    {
        var parser = new JiangsuBiddingPlatformParser();
        var request = new BiddingCollectionRequest
        {
            CollectionId = "jiangsu-uri",
            Keywords = ["云计算", "软件采购"],
            FromDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.FromHours(8)),
            ToDate = new DateTimeOffset(2025, 3, 31, 23, 59, 59, TimeSpan.FromHours(8)),
            MaxResults = 20
        };

        var uri = parser.BuildSearchUri(request);

        Assert.Equal("api.jszbtb.com", uri.Host);
        Assert.Equal("/DataGatewayApi/PublishBulletins", uri.AbsolutePath);
        Assert.Contains("keyword=%E4%BA%91%E8%AE%A1%E7%AE%97%20%E8%BD%AF%E4%BB%B6%E9%87%87%E8%B4%AD", uri.Query);
        Assert.Contains("pageSize=20", uri.Query);
        Assert.Contains("startTime=2025-01-01%2000%3A00%3A00", uri.Query);
        Assert.Contains("endTime=2025-03-31%2023%3A59%3A59", uri.Query);
    }

    [Fact]
    public async Task ParseAsync_maps_public_fields_and_omits_personal_data()
    {
        const string json = """
            {
              "errorMessage": "",
              "data": {
                "totalCount": 1,
                "totalPage": 1,
                "currentPage": 1,
                "pageSize": 1,
                "data": [{
                  "tenderProjectCode": "Z320116J001J08774001",
                  "bulletinID": "2c91808b94f30cc30195ec3a48b17f44",
                  "bulletinName": "六合区新华四村55栋旁招租项目招租公告（第二次）",
                  "industryName": "其他",
                  "regionName": "江苏省南京市江北新区",
                  "noticeSendTime": "2025-03-31 20:42:39",
                  "bulletinType": 1,
                  "bulletinSourceName": "工具发布",
                  "medium": "江苏省招标投标公共服务平台",
                  "projectCompany": null,
                  "contactName": "不应进入结果",
                  "contactPhone": "13800138000"
                }]
              },
              "success": true
            }
            """;
        var parser = new JiangsuBiddingPlatformParser();
        var request = CreateRequest();

        var notices = await parser.ParseAsync(json, request, CancellationToken.None);

        var notice = Assert.Single(notices);
        Assert.Equal("jszbtb.com", notice.SourcePlatform);
        Assert.Equal("江苏省招标投标公共服务平台", notice.Publisher);
        Assert.Equal("江苏省南京市江北新区", notice.Region);
        Assert.Equal("其他", notice.Industry);
        Assert.Equal(TimeSpan.FromHours(8), notice.PublishedAt.Offset);
        Assert.Contains("2c91808b94f30cc30195ec3a48b17f44", notice.NoticeUrl);
        Assert.Null(notice.Validate());
    }

    [Fact]
    public async Task ParseAsync_rejects_unsuccessful_or_malformed_payloads()
    {
        var parser = new JiangsuBiddingPlatformParser();
        var request = CreateRequest();

        await Assert.ThrowsAsync<FormatException>(() =>
            parser.ParseAsync("{\"success\":false}", request, CancellationToken.None));
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() =>
            parser.ParseAsync("{not-json}", request, CancellationToken.None));
    }

    private static BiddingCollectionRequest CreateRequest() => new()
    {
        CollectionId = "jiangsu-parser",
        Keywords = ["招标"],
        FromDate = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        ToDate = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
        MaxResults = 10
    };
}
