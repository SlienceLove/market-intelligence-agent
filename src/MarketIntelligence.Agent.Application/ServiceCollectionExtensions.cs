using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Application.Notifications;

namespace MarketIntelligence.Agent.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        AddBidding(services);
        services.AddSingleton<IChannelMediaCollector, UnconfiguredChannelMediaCollector>();
        services.AddSingleton<ITranscriptionService, UnconfiguredTranscriptionService>();
        services.AddSingleton<IFrameOcrService, UnconfiguredFrameOcrService>();
        services.AddSingleton<ISpeechSynthesisService, UnconfiguredSpeechSynthesisService>();
        services.AddSingleton<IVideoCompositionService, UnconfiguredVideoCompositionService>();
        services.AddSingleton<InMemoryMediaJobCoordinator>();
        services.AddSingleton<IMediaJobCoordinator>(servicesProvider =>
            servicesProvider.GetRequiredService<InMemoryMediaJobCoordinator>());
        services.AddHostedService(servicesProvider =>
            servicesProvider.GetRequiredService<InMemoryMediaJobCoordinator>());
        return services;
    }

    /// <summary>
    /// Bidding defaults. Every channel and collector defaults to the safe-failure
    /// adapter, and the plan source defaults to empty, so a host that has configured
    /// nothing collects nothing and pushes nothing. Infrastructure overrides these.
    /// </summary>
    private static void AddBidding(IServiceCollection services)
    {
        services.TryAddSingleton<IBiddingNoticeCollector, UnconfiguredBiddingNoticeCollector>();
        services.TryAddSingleton<IBiddingNoticeLedger, InMemoryNoticeLedger>();
        services.TryAddSingleton<IScheduledCollectionHistory, InMemoryScheduledCollectionHistory>();
        services.TryAddSingleton<IScheduledCollectionPlanSource>(
            _ => new InMemoryScheduledCollectionPlanSource());

        services.TryAddKeyedSingleton<INotificationChannel, UnconfiguredNotificationChannel>(
            ScheduledNotificationChannels.Smtp);
        services.TryAddKeyedSingleton<INotificationChannel, UnconfiguredNotificationChannel>(
            ScheduledNotificationChannels.Webhook);

        services.TryAddSingleton<IScheduledCollectionCoordinator>(serviceProvider =>
            new ScheduledCollectionCoordinator(
                serviceProvider.GetRequiredService<IBiddingNoticeCollector>(),
                serviceProvider.GetRequiredService<IBiddingNoticeLedger>(),
                serviceProvider.GetRequiredKeyedService<INotificationChannel>(
                    ScheduledNotificationChannels.Smtp),
                serviceProvider.GetRequiredKeyedService<INotificationChannel>(
                    ScheduledNotificationChannels.Webhook),
                serviceProvider.GetRequiredService<IScheduledCollectionHistory>(),
                serviceProvider.GetRequiredService<IScheduledCollectionPlanSource>(),
                serviceProvider.GetRequiredService<ILogger<ScheduledCollectionCoordinator>>()));
    }
}
