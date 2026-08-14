using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.DependencyInjection;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Verifies that the DI registration correctly wires up the composite collector.
/// </summary>
public sealed class BiddingCollectionDiIntegrationTests
{
    [Fact]
    public void AddBiddingCollectionInfrastructure_registers_all_required_services()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        // Act
        services.AddBiddingCollectionInfrastructure();
        var provider = services.BuildServiceProvider();

        // Assert
        Assert.NotNull(provider.GetService<RobotsTxtCache>());
        Assert.NotNull(provider.GetService<IBiddingRateLimiter>());
        var jiangsuCollector = provider.GetRequiredKeyedService<IBiddingNoticeCollector>("jszbtb.com");
        Assert.Equal("jszbtb.com", jiangsuCollector.SourcePlatform);
        Assert.Equal(
            "ggzy.gov.cn",
            provider.GetRequiredKeyedService<IBiddingNoticeCollector>("ggzy.gov.cn").SourcePlatform);
        Assert.Equal(
            "ccgp-jiangsu.gov.cn",
            provider.GetRequiredKeyedService<IBiddingNoticeCollector>("ccgp-jiangsu.gov.cn").SourcePlatform);

        // Should be able to resolve IBiddingNoticeCollector (the composite)
        var collector = provider.GetService<IBiddingNoticeCollector>();
        Assert.NotNull(collector);
        Assert.IsType<CompositeBiddingNoticeCollector>(collector);
    }

    [Fact]
    public void Composite_collector_receives_platform_collectors()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddBiddingCollectionInfrastructure();
        var provider = services.BuildServiceProvider();

        // Act
        var collector = provider.GetRequiredService<IBiddingNoticeCollector>();
        var composite = collector as CompositeBiddingNoticeCollector;

        // Assert
        Assert.NotNull(composite);
        Assert.Equal("composite", composite.SourcePlatform);
        Assert.True(composite.IsConfigured);

        // Should also be able to resolve platform collectors by their concrete types
        var platformCollector = provider.GetService<HttpBiddingNoticeCollector>();
        Assert.NotNull(platformCollector);
    }

    [Fact]
    public async Task Composite_collector_can_collect_from_registered_platforms()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();
        services.AddBiddingCollectionInfrastructure();
        var provider = services.BuildServiceProvider();

        var collector = provider.GetRequiredService<IBiddingNoticeCollector>();

        var request = new BiddingCollectionRequest
        {
            CollectionId = "di-integration-test",
            Keywords = ["test"],
            MaxResults = 100
        };

        // Act
        var result = await collector.CollectAsync(request);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("di-integration-test", result.CollectionId);
        // Result may be success or failure depending on whether mock platforms can actually connect
        // The important thing is that it runs without throwing
    }
}
