using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Notifications;
using MarketIntelligence.Agent.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Proves the scheduled-collection graph resolves and that a host which has
/// configured nothing cannot collect or push.
/// </summary>
public sealed class ScheduledCollectionWiringTests
{
    [Fact]
    public void Coordinator_ResolvesFromApplicationDefaults()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredService<IScheduledCollectionCoordinator>());
    }

    [Fact]
    public void BothChannelKeys_Resolve()
    {
        using var provider = BuildProvider();

        Assert.NotNull(provider.GetRequiredKeyedService<INotificationChannel>(
            ScheduledNotificationChannels.Smtp));
        Assert.NotNull(provider.GetRequiredKeyedService<INotificationChannel>(
            ScheduledNotificationChannels.Webhook));
    }

    [Fact]
    public void DefaultConfiguration_LeavesEveryChannelUnconfigured()
    {
        using var provider = BuildProvider();

        Assert.False(provider
            .GetRequiredKeyedService<INotificationChannel>(ScheduledNotificationChannels.Smtp)
            .IsConfigured);
        Assert.False(provider
            .GetRequiredKeyedService<INotificationChannel>(ScheduledNotificationChannels.Webhook)
            .IsConfigured);
    }

    [Fact]
    public void DefaultConfiguration_LeavesCollectorUnconfigured()
    {
        using var provider = BuildProvider();

        Assert.False(provider.GetRequiredService<IBiddingNoticeCollector>().IsConfigured);
    }

    [Fact]
    public async Task DefaultConfiguration_SchedulesNoPlans()
    {
        using var provider = BuildProvider();

        var plans = await provider.GetRequiredService<IScheduledCollectionPlanSource>().GetPlansAsync();

        Assert.Empty(plans);
    }

    [Fact]
    public async Task DefaultConfiguration_ExecutingDuePlansPushesNothing()
    {
        using var provider = BuildProvider();

        var results = await provider.GetRequiredService<IScheduledCollectionCoordinator>()
            .ExecuteDuePlansAsync(new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero));

        Assert.Empty(results);
    }

    [Fact]
    public async Task DefaultConfiguration_UnconfiguredCollectorFailsSafely()
    {
        using var provider = BuildProvider();

        var plan = new ScheduledCollectionPlan
        {
            PlanId = "wiring-plan",
            Name = "Wiring probe",
            Keywords = ["测试"],
            NotificationChannel = ScheduledNotificationChannels.Smtp,
            ExecutionTimeUtc = new TimeOnly(0, 0)
        };

        var result = await provider.GetRequiredService<IScheduledCollectionCoordinator>()
            .ExecuteAsync(plan, new DateOnly(2026, 8, 12));

        Assert.Equal(ScheduledCollectionStatus.Failed, result.Status);
        Assert.Equal("bidding_source_not_configured", result.FailureCode);
        Assert.Equal(0, result.NoticesNotified);
    }

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddApplication();
        services.AddInfrastructure();
        return services.BuildServiceProvider();
    }
}
