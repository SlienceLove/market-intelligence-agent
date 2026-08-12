using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
                    "MarketIntelligence-Agent/1.0 (+https://github.com/your-org/market-intelligence-agent)");
            });

        // Register HTTP client for robots.txt fetching
        services.AddHttpClient(nameof(RobotsTxtCache))
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(10);
            });

        // Register platform parsers (keyed services)
        services.AddKeyedSingleton<IPlatformParser, MockRssPlatformParser>("mock-rss");
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

        // Add platform collector descriptors to the service collection
        foreach (var descriptor in platformCollectorDescriptors)
        {
            services.Add(descriptor);
        }

        // Register composite collector that wraps all platform collectors
        // Resolve platform collectors by their concrete types to avoid circular dependency
        services.AddSingleton<IBiddingNoticeCollector>(sp =>
        {
            var platformCollectors = new List<IBiddingNoticeCollector>
            {
                sp.GetRequiredService<HttpBiddingNoticeCollector>()
            };

            var logger = sp.GetRequiredService<ILogger<CompositeBiddingNoticeCollector>>();

            return new CompositeBiddingNoticeCollector(platformCollectors, logger);
        });

        return services;
    }
}
