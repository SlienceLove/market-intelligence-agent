using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Bidding;

namespace MarketIntelligence.Agent.Tests;

public sealed class PublicHtmlBiddingPlatformParserTests
{
    [Fact]
    public async Task National_parser_maps_public_home_page_links_and_filters_keywords()
    {
        const string html = """
            <ul>
              <li><a href="/information/deal/html/a/530000/0101/20260814/00538206.html">新屯光伏发电项目工程总承包招标公告</a></li>
              <li><a href="/information/deal/html/a/130000/2201/20260814/0013fdd5.html">氮氧化物排污权交易公告</a></li>
            </ul>
            """;
        var parser = new NationalPublicResourcePlatformParser();

        var notices = await parser.ParseAsync(
            html,
            CreateRequest("光伏"),
            CancellationToken.None);

        var notice = Assert.Single(notices);
        Assert.Equal("ggzy.gov.cn", notice.SourcePlatform);
        Assert.Equal("新屯光伏发电项目工程总承包招标公告", notice.Title);
        Assert.Equal(new DateTime(2026, 8, 14), notice.PublishedAt.Date);
        Assert.StartsWith("https://www.ggzy.gov.cn/information/deal/", notice.NoticeUrl);
        Assert.Null(notice.Validate());
    }

    [Fact]
    public async Task Jiangsu_procurement_parser_maps_public_rows_without_detail_content()
    {
        const string html = """
            <table>
              <tr>
                <td class="col-title"><a href="/jiangsu/js_cggg/details.html?gglb=gkzb&amp;ggid=932b5ad7">云计算服务采购公告</a></td>
                <td class="col-date">2026-08-14</td>
              </tr>
              <tr>
                <td><a href="/jiangsu/js_cggg/details.html?gglb=gkzb&amp;ggid=other">办公家具采购公告</a></td>
                <td>2026-08-14</td>
              </tr>
            </table>
            """;
        var parser = new JiangsuGovernmentProcurementParser();

        var notices = await parser.ParseAsync(
            html,
            CreateRequest("云计算"),
            CancellationToken.None);

        var notice = Assert.Single(notices);
        Assert.Equal("ccgp-jiangsu.gov.cn", notice.SourcePlatform);
        Assert.Equal("江苏省", notice.Region);
        Assert.Contains("ggid=932b5ad7", notice.NoticeUrl);
        Assert.Null(notice.Validate());
    }

    private static BiddingCollectionRequest CreateRequest(string keyword) => new()
    {
        CollectionId = "public-html-parser",
        Keywords = [keyword],
        MaxResults = 10
    };
}
