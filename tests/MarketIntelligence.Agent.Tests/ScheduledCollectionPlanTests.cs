using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketIntelligence.Agent.Tests;

public sealed class ScheduledCollectionPlanTests
{
    private static readonly DateTimeOffset Wednesday0900Utc = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsDueAt_BeforeExecutionTime_IsNotDue()
    {
        var plan = CreatePlan() with { ExecutionTimeUtc = new TimeOnly(9, 0) };

        Assert.False(plan.IsDueAt(Wednesday0900Utc.AddMinutes(-1)));
    }

    [Fact]
    public void IsDueAt_AtExecutionTime_IsDue()
    {
        var plan = CreatePlan() with { ExecutionTimeUtc = new TimeOnly(9, 0) };

        Assert.True(plan.IsDueAt(Wednesday0900Utc));
    }

    [Fact]
    public void IsDueAt_AfterExecutionTime_IsStillDue()
    {
        // A worker that was down at 09:00 must still run the slot when it comes back.
        var plan = CreatePlan() with { ExecutionTimeUtc = new TimeOnly(9, 0) };

        Assert.True(plan.IsDueAt(Wednesday0900Utc.AddHours(5)));
    }

    [Fact]
    public void IsDueAt_DisabledPlan_IsNeverDue()
    {
        var plan = CreatePlan() with { Enabled = false, ExecutionTimeUtc = new TimeOnly(0, 0) };

        Assert.False(plan.IsDueAt(Wednesday0900Utc));
    }

    [Fact]
    public void IsDueAt_DayNotInSet_IsNotDue()
    {
        var plan = CreatePlan() with
        {
            ExecutionTimeUtc = new TimeOnly(9, 0),
            DaysOfWeek = new HashSet<DayOfWeek> { DayOfWeek.Monday }
        };

        Assert.Equal(DayOfWeek.Wednesday, Wednesday0900Utc.DayOfWeek);
        Assert.False(plan.IsDueAt(Wednesday0900Utc));
    }

    [Fact]
    public void IsDueAt_DayInSet_IsDue()
    {
        var plan = CreatePlan() with
        {
            ExecutionTimeUtc = new TimeOnly(9, 0),
            DaysOfWeek = new HashSet<DayOfWeek> { DayOfWeek.Wednesday }
        };

        Assert.True(plan.IsDueAt(Wednesday0900Utc));
    }

    [Fact]
    public void IsDueAt_EvaluatesInUtc_NotLocalTime()
    {
        var plan = CreatePlan() with { ExecutionTimeUtc = new TimeOnly(9, 0) };

        // 08:30Z expressed as 10:30+02:00: local wall clock is past 09:00, UTC is not.
        var beforeInUtc = new DateTimeOffset(2026, 8, 12, 10, 30, 0, TimeSpan.FromHours(2));

        Assert.False(plan.IsDueAt(beforeInUtc));
    }

    [Theory]
    [InlineData("", "invalid_plan_id")]
    [InlineData("has space", "invalid_plan_id")]
    public void Validate_RejectsMalformedPlanId(string planId, string expected)
    {
        Assert.Equal(expected, (CreatePlan() with { PlanId = planId }).Validate());
    }

    [Fact]
    public void Validate_RejectsEmptyKeywords()
    {
        Assert.Equal("keyword_required", (CreatePlan() with { Keywords = [] }).Validate());
    }

    [Fact]
    public void Validate_RejectsBlankKeyword()
    {
        Assert.Equal("invalid_keyword", (CreatePlan() with { Keywords = ["ok", "  "] }).Validate());
    }

    [Fact]
    public void Validate_RejectsMaxResultsAboveCeiling()
    {
        var plan = CreatePlan() with { MaxResults = BiddingContractLimits.MaxResultsCeiling + 1 };

        Assert.Equal("invalid_max_results", plan.Validate());
    }

    [Fact]
    public void Validate_RejectsNonPositiveLookback()
    {
        Assert.Equal("invalid_time_window", (CreatePlan() with { LookbackDays = 0 }).Validate());
    }

    [Fact]
    public void Validate_RejectsUnknownChannel()
    {
        var plan = CreatePlan() with { NotificationChannel = "carrier-pigeon" };

        Assert.Equal("invalid_notification_channel", plan.Validate());
    }

    [Fact]
    public void Validate_AcceptsWellFormedPlan()
    {
        Assert.Null(CreatePlan().Validate());
    }

    [Fact]
    public async Task ExecuteDuePlansAsync_RunsOnlyDuePlans()
    {
        var duePlan = CreatePlan() with { PlanId = "due-plan", ExecutionTimeUtc = new TimeOnly(8, 0) };
        var futurePlan = CreatePlan() with { PlanId = "future-plan", ExecutionTimeUtc = new TimeOnly(23, 0) };
        var coordinator = CreateCoordinator(new InMemoryScheduledCollectionPlanSource([duePlan, futurePlan]));

        var results = await coordinator.ExecuteDuePlansAsync(Wednesday0900Utc);

        Assert.Single(results);
        Assert.Equal("due-plan", results[0].PlanId);
    }

    [Fact]
    public async Task ExecuteDuePlansAsync_NoPlansConfigured_RunsNothing()
    {
        var coordinator = CreateCoordinator(new InMemoryScheduledCollectionPlanSource());

        Assert.Empty(await coordinator.ExecuteDuePlansAsync(Wednesday0900Utc));
    }

    [Fact]
    public async Task ExecuteDuePlansAsync_SameInstantTwice_DoesNotDoubleNotify()
    {
        var duePlan = CreatePlan() with { ExecutionTimeUtc = new TimeOnly(8, 0) };
        var channel = new CountingChannel();
        var coordinator = CreateCoordinator(
            new InMemoryScheduledCollectionPlanSource([duePlan]), channel);

        await coordinator.ExecuteDuePlansAsync(Wednesday0900Utc);
        await coordinator.ExecuteDuePlansAsync(Wednesday0900Utc.AddHours(1));

        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task ExecuteDuePlansAsync_OnePlanFailing_DoesNotStopTheOthers()
    {
        var failing = CreatePlan() with { PlanId = "bad-plan", MaxResults = 0, ExecutionTimeUtc = new TimeOnly(8, 0) };
        var healthy = CreatePlan() with { PlanId = "good-plan", ExecutionTimeUtc = new TimeOnly(8, 0) };
        var coordinator = CreateCoordinator(new InMemoryScheduledCollectionPlanSource([failing, healthy]));

        var results = await coordinator.ExecuteDuePlansAsync(Wednesday0900Utc);

        Assert.Equal(2, results.Count);
        Assert.Equal("invalid_max_results", results.Single(r => r.PlanId == "bad-plan").FailureCode);
        Assert.True(results.Single(r => r.PlanId == "good-plan").Succeeded);
    }

    [Fact]
    public async Task ExecuteDuePlansAsync_CancelledToken_Throws()
    {
        var duePlan = CreatePlan() with { ExecutionTimeUtc = new TimeOnly(8, 0) };
        var coordinator = CreateCoordinator(new InMemoryScheduledCollectionPlanSource([duePlan]));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ExecuteDuePlansAsync(Wednesday0900Utc, cancellation.Token));
    }

    private static ScheduledCollectionPlan CreatePlan() =>
        new()
        {
            PlanId = "plan-001",
            Name = "Daily bidding digest",
            Keywords = ["测试"],
            LookbackDays = 7,
            MaxResults = 50,
            NotificationChannel = ScheduledNotificationChannels.Smtp,
            ExecutionTimeUtc = new TimeOnly(9, 0),
            Enabled = true
        };

    private static ScheduledCollectionCoordinator CreateCoordinator(
        IScheduledCollectionPlanSource planSource,
        INotificationChannel? channel = null)
    {
        channel ??= new CountingChannel();

        return new ScheduledCollectionCoordinator(
            new FakeBiddingNoticeCollector(),
            new InMemoryNoticeLedger(),
            channel,
            channel,
            new InMemoryScheduledCollectionHistory(),
            planSource,
            NullLogger<ScheduledCollectionCoordinator>.Instance);
    }

    private sealed class CountingChannel : INotificationChannel
    {
        public int SendCount { get; private set; }

        public bool IsConfigured => true;

        public Task<NotificationResult> SendAsync(
            NotificationMessage message, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.FromResult(NotificationResult.Success($"notif-{SendCount:D4}"));
        }
    }
}
