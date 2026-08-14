using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Registers bidding collection infrastructure services.
/// </summary>
public static class BiddingCollectionServiceCollectionExtensions
{
    /// <summary>
    /// Registers bidding collection infrastructure services:
    /// robots.txt cache, rate limiter, HTTP collectors, and the composite collector.
    /// </summary>
    public static IServiceCollection AddBiddingCollectionInfrastructure(
        this IServiceCollection services)
    {
        services.AddOptions<BiddingOptions>();

        // Register shared infrastructure
        services.AddSingleton<RobotsTxtCache>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(RobotsTxtCache));
            return new RobotsTxtCache(httpClient);
        });

        services.AddSingleton<IBiddingRateLimiter, BiddingRateLimiter>();

        // Register HTTP client for bidding collection with 30s timeout
        services.AddHttpClient(nameof(HttpBiddingNoticeCollector))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "MarketIntelligenceAgent/1.0 (+https://github.com/SlienceLove/market-intelligence-agent)");
            });

        // Register HTTP client for robots.txt fetching
        services.AddHttpClient(nameof(RobotsTxtCache))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "MarketIntelligenceAgent/1.0 (+https://github.com/SlienceLove/market-intelligence-agent)");
            });

        // Register platform parsers (keyed services)
        services.AddKeyedSingleton<IPlatformParser, MockRssPlatformParser>("mock-rss");
        services.AddKeyedSingleton<IPlatformParser, JiangsuBiddingPlatformParser>("jszbtb.com");
        services.AddKeyedSingleton<IPlatformParser, NationalPublicResourcePlatformParser>("ggzy.gov.cn");
        services.AddKeyedSingleton<IPlatformParser, JiangsuGovernmentProcurementParser>("ccgp-jiangsu.gov.cn");
        services.AddKeyedSingleton<IPlatformParser>("unconfigured-example",
            (sp, key) => new UnconfiguredPlatformParser("unconfigured-example"));

        // Register individual platform collectors in a separate list
        // to avoid circular dependency with composite collector
        var platformCollectorDescriptors = new List<ServiceDescriptor>();

        // Create mock-rss collector descriptor
        platformCollectorDescriptors.Add(ServiceDescriptor.Singleton<HttpBiddingNoticeCollector>(sp =>
        {
            var parser = sp.GetRequiredKeyedService<IPlatformParser>("mock-rss");
            var robotsCache = sp.GetRequiredService<RobotsTxtCache>();
            var rateLimiter = sp.GetRequiredService<IBiddingRateLimiter>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(HttpBiddingNoticeCollector));
            var logger = sp.GetRequiredService<ILogger<HttpBiddingNoticeCollector>>();

            return new HttpBiddingNoticeCollector(parser, robotsCache, rateLimiter, httpClient, logger);
        }));

        services.AddKeyedSingleton<IBiddingNoticeCollector>("jszbtb.com", (sp, _) =>
        {
            var parser = sp.GetRequiredKeyedService<IPlatformParser>("jszbtb.com");
            var robotsCache = sp.GetRequiredService<RobotsTxtCache>();
            var rateLimiter = sp.GetRequiredService<IBiddingRateLimiter>();
            var httpClient = sp.GetRequiredService<IHttpClientFactory>()
                .CreateClient(nameof(HttpBiddingNoticeCollector));
            var logger = sp.GetRequiredService<ILogger<HttpBiddingNoticeCollector>>();

            return new HttpBiddingNoticeCollector(parser, robotsCache, rateLimiter, httpClient, logger);
        });

        services.AddKeyedSingleton<IBiddingNoticeCollector>("ggzy.gov.cn", (sp, _) =>
            CreatePlatformCollector(sp, "ggzy.gov.cn"));
        services.AddKeyedSingleton<IBiddingNoticeCollector>("ccgp-jiangsu.gov.cn", (sp, _) =>
            CreatePlatformCollector(sp, "ccgp-jiangsu.gov.cn"));

        // Add platform collector descriptors to the service collection
        foreach (var descriptor in platformCollectorDescriptors)
        {
            services.Add(descriptor);
        }

        // Register composite collector that wraps all platform collectors
        // Resolve platform collectors by their concrete types to avoid circular dependency
        services.AddSingleton<IBiddingNoticeCollector>(sp =>
        {
            var platformCollectors = new List<IBiddingNoticeCollector>();
            var enabledPlatforms = sp.GetRequiredService<IOptions<BiddingOptions>>()
                .Value.Collector.EnabledPlatforms;
            if (enabledPlatforms.Any(platform =>
                    string.Equals(platform, "jszbtb.com", StringComparison.OrdinalIgnoreCase)))
            {
                platformCollectors.Add(
                    sp.GetRequiredKeyedService<IBiddingNoticeCollector>("jszbtb.com"));
            }
            if (enabledPlatforms.Any(platform =>
                    string.Equals(platform, "ggzy.gov.cn", StringComparison.OrdinalIgnoreCase)))
            {
                platformCollectors.Add(
                    sp.GetRequiredKeyedService<IBiddingNoticeCollector>("ggzy.gov.cn"));
            }
            if (enabledPlatforms.Any(platform =>
                    string.Equals(platform, "ccgp-jiangsu.gov.cn", StringComparison.OrdinalIgnoreCase)))
            {
                platformCollectors.Add(
                    sp.GetRequiredKeyedService<IBiddingNoticeCollector>("ccgp-jiangsu.gov.cn"));
            }

            // Preserve the existing local/mock behavior when no real platform is
            // explicitly enabled. Enabling a real platform removes the mock from
            // the aggregate result rather than mixing fixture notices into live data.
            if (platformCollectors.Count == 0)
            {
                platformCollectors.Add(sp.GetRequiredService<HttpBiddingNoticeCollector>());
            }

            var logger = sp.GetRequiredService<ILogger<CompositeBiddingNoticeCollector>>();

            return new CompositeBiddingNoticeCollector(platformCollectors, logger);
        });

        return services;
    }

    private static HttpBiddingNoticeCollector CreatePlatformCollector(
        IServiceProvider serviceProvider,
        string platformId)
    {
        var parser = serviceProvider.GetRequiredKeyedService<IPlatformParser>(platformId);
        var robotsCache = serviceProvider.GetRequiredService<RobotsTxtCache>();
        var rateLimiter = serviceProvider.GetRequiredService<IBiddingRateLimiter>();
        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(nameof(HttpBiddingNoticeCollector));
        var logger = serviceProvider.GetRequiredService<ILogger<HttpBiddingNoticeCollector>>();

        return new HttpBiddingNoticeCollector(parser, robotsCache, rateLimiter, httpClient, logger);
    }
}
