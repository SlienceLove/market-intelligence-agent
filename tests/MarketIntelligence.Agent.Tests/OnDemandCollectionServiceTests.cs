using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketIntelligence.Agent.Tests;

public sealed class OnDemandCollectionServiceTests
{
    private static readonly DateOnly FixedDate = new(2026, 8, 12);

    // ── helpers ──────────────────────────────────────────────────────────────

    private static OnDemandCollectionService CreateService(
        IScheduledCollectionCoordinator? coordinator = null,
        IScheduledCollectionPlanSource? planSource = null) =>
        new(
            coordinator ?? new StubCoordinator(),
            planSource ?? new StubPlanSource(),
            NullLogger<OnDemandCollectionService>.Instance);

    private static ScheduledCollectionPlan CreatePlan(string id = "plan-001") =>
        new()
        {
            PlanId = id,
            Name = $"Plan {id}",
            Keywords = ["测试"],
            LookbackDays = 1,
            MaxResults = 50,
            NotificationChannel = ScheduledNotificationChannels.Smtp,
            ExecutionTimeUtc = new TimeOnly(9, 0),
            Enabled = true
        };

    // ── test cases ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoPlanIds_RunsAllRegisteredPlans()
    {
        var coordinator = new TrackingCoordinator();
        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2"), CreatePlan("p3")));

        await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal(["p1", "p2", "p3"], coordinator.ExecutedPlanIds);
    }

    [Fact]
    public async Task ExecuteAsync_WithPlanIds_RunsOnlySpecifiedPlans()
    {
        var coordinator = new TrackingCoordinator();
        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2"), CreatePlan("p3")));

        await service.ExecuteAsync(new CollectOnDemandRequest { PlanIds = ["p1", "p3"] });

        Assert.Equal(["p1", "p3"], coordinator.ExecutedPlanIds);
    }

    [Fact]
    public async Task ExecuteAsync_AsOfOverride_PassesCustomDateToCoordinator()
    {
        var coordinator = new TrackingCoordinator();
        var customDate = new DateOnly(2026, 7, 1);
        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1")));

        await service.ExecuteAsync(new CollectOnDemandRequest { AsOf = customDate });

        Assert.Equal(customDate, coordinator.LastExecutionDate);
    }

    [Fact]
    public async Task ExecuteAsync_AllSucceed_ReturnsSuccessStatus()
    {
        var service = CreateService(
            coordinator: new StubCoordinator(succeedAll: true),
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2")));

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal("success", response.Status);
        Assert.Equal(2, response.PlansExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_AllFail_ReturnsFailedStatus()
    {
        var service = CreateService(
            coordinator: new StubCoordinator(succeedAll: false),
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2")));

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal("failed", response.Status);
        Assert.Equal(2, response.PlansExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_MixedOutcomes_ReturnsPartialStatus()
    {
        int callIndex = 0;
        var coordinator = new StubCoordinator(factory: (plan, date) =>
        {
            callIndex++;
            return callIndex == 1
                ? ScheduledCollectionResult.Success(plan.PlanId, date, 3, 2, 2, "notif-001")
                : ScheduledCollectionResult.Failed(plan.PlanId, date, "provider_unavailable");
        });

        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2")));

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal("partial", response.Status);
        Assert.Equal(2, response.PlansExecuted);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyPlanSource_ReturnsFailedWithZeroPlans()
    {
        var service = CreateService(planSource: new StubPlanSource());

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal("failed", response.Status);
        Assert.Equal(0, response.PlansExecuted);
        Assert.Empty(response.Plans);
    }

    [Fact]
    public async Task ExecuteAsync_AggregatesNoticeCountsAcrossPlans()
    {
        var coordinator = new StubCoordinator(factory: (plan, date) =>
            ScheduledCollectionResult.Success(plan.PlanId, date, 5, 4, 4, "notif-x"));

        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2")));

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        // Each plan collects 5, but coordinator returns NoticesCollected = 5 per plan
        Assert.Equal(10, response.TotalNoticesCollected);
        Assert.Equal(2, response.Plans.Count);
    }

    [Fact]
    public async Task ExecuteAsync_AllSkipped_ReturnsSuccessStatus()
    {
        var coordinator = new StubCoordinator(factory: (plan, date) =>
            ScheduledCollectionResult.Skipped(plan.PlanId, date, "already_running"));

        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2")));

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal("success", response.Status);
        Assert.Equal(2, response.PlansExecuted);
        Assert.Equal(2, response.SkippedCount);
    }

    [Fact]
    public async Task ExecuteAsync_MixedSkippedAndFailed_ReturnsPartialStatus()
    {
        var callIndex = 0;
        var coordinator = new StubCoordinator(factory: (plan, date) =>
        {
            callIndex++;
            return callIndex == 1
                ? ScheduledCollectionResult.Skipped(plan.PlanId, date, "already_running")
                : ScheduledCollectionResult.Failed(plan.PlanId, date, "provider_unavailable");
        });

        var service = CreateService(
            coordinator: coordinator,
            planSource: new StubPlanSource(CreatePlan("p1"), CreatePlan("p2")));

        var response = await service.ExecuteAsync(new CollectOnDemandRequest());

        Assert.Equal("partial", response.Status);
        Assert.Equal(2, response.PlansExecuted);
        Assert.Equal(1, response.SkippedCount);
    }

    // ── test doubles ─────────────────────────────────────────────────────────

    private sealed class StubPlanSource(params ScheduledCollectionPlan[] plans)
        : IScheduledCollectionPlanSource
    {
        public Task<IReadOnlyList<ScheduledCollectionPlan>> GetPlansAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledCollectionPlan>>(plans);
    }

    /// <summary>Configurable coordinator for simple success/fail scenarios.</summary>
    private sealed class StubCoordinator : IScheduledCollectionCoordinator
    {
        private readonly Func<ScheduledCollectionPlan, DateOnly, ScheduledCollectionResult> _factory;

        public StubCoordinator(
            bool succeedAll = true,
            Func<ScheduledCollectionPlan, DateOnly, ScheduledCollectionResult>? factory = null)
        {
            _factory = factory ?? ((plan, date) => succeedAll
                ? ScheduledCollectionResult.Success(plan.PlanId, date, 3, 2, 2, "notif-stub")
                : ScheduledCollectionResult.Failed(plan.PlanId, date, "provider_unavailable"));
        }

        public Task<ScheduledCollectionResult> ExecuteAsync(
            ScheduledCollectionPlan plan,
            DateOnly executionDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_factory(plan, executionDate));

        public Task<IReadOnlyList<ScheduledCollectionResult>> ExecuteDuePlansAsync(
            DateTimeOffset currentTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledCollectionResult>>([]);
    }

    /// <summary>Coordinator that records which plans ran and the last date used.</summary>
    private sealed class TrackingCoordinator : IScheduledCollectionCoordinator
    {
        public List<string> ExecutedPlanIds { get; } = [];
        public DateOnly? LastExecutionDate { get; private set; }

        public Task<ScheduledCollectionResult> ExecuteAsync(
            ScheduledCollectionPlan plan,
            DateOnly executionDate,
            CancellationToken cancellationToken = default)
        {
            ExecutedPlanIds.Add(plan.PlanId);
            LastExecutionDate = executionDate;
            return Task.FromResult(
                ScheduledCollectionResult.Success(plan.PlanId, executionDate, 1, 1, 1, "notif-track"));
        }

        public Task<IReadOnlyList<ScheduledCollectionResult>> ExecuteDuePlansAsync(
            DateTimeOffset currentTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ScheduledCollectionResult>>([]);
    }
}
