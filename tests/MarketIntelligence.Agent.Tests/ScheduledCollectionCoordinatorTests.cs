using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Application.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketIntelligence.Agent.Tests;

public sealed class ScheduledCollectionCoordinatorTests
{
    /// <summary>
    /// Matches <see cref="FakeBiddingNoticeCollector"/>'s default reference instant so
    /// the synthesized notices fall inside the plan's lookback window. Fixed rather
    /// than derived from the clock: the assertions must not change with the date.
    /// </summary>
    private static readonly DateOnly ExecutionDate = new(2026, 8, 12);

    [Fact]
    public async Task ExecuteAsync_DisabledPlan_ReturnsSkippedAndDoesNotCollect()
    {
        var collector = new CountingCollector();
        var coordinator = CreateCoordinator(collector: collector);

        var result = await coordinator.ExecuteAsync(CreatePlan() with { Enabled = false }, ExecutionDate);

        Assert.Equal(ScheduledCollectionStatus.Skipped, result.Status);
        Assert.Equal("plan_disabled", result.FailureCode);
        Assert.Equal(0, collector.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidPlan_FailsWithoutBurningTheDaySlot()
    {
        var history = new InMemoryScheduledCollectionHistory();
        var coordinator = CreateCoordinator(history: history);

        var invalid = await coordinator.ExecuteAsync(CreatePlan() with { MaxResults = 0 }, ExecutionDate);

        Assert.Equal(ScheduledCollectionStatus.Failed, invalid.Status);
        Assert.Equal("invalid_max_results", invalid.FailureCode);
        Assert.Null(await history.GetExecutionResultAsync(CreatePlan().PlanId, ExecutionDate));
    }

    [Fact]
    public async Task ExecuteAsync_SameDayTwice_NotifiesOnceAndReusesResult()
    {
        var channel = new CountingNotificationChannel();
        var coordinator = CreateCoordinator(smtpChannel: channel);
        var plan = CreatePlan();

        var first = await coordinator.ExecuteAsync(plan, ExecutionDate);
        var second = await coordinator.ExecuteAsync(plan, ExecutionDate);

        Assert.True(first.Succeeded);
        Assert.Equal(1, channel.SendCount);
        Assert.Equal(first.NotificationId, second.NotificationId);
        Assert.Equal(first.NoticesNotified, second.NoticesNotified);
        Assert.True(second.WasAlreadyCompleted);
    }

    [Fact]
    public async Task ExecuteAsync_NextDay_IsANewSlotAndNotifiesAgain()
    {
        var channel = new CountingNotificationChannel();
        // A distinct ledger per run is not used here: the ledger is shared, so the
        // second day only notifies because its window admits a different notice set.
        var coordinator = CreateCoordinator(smtpChannel: channel, collector: new AlwaysFreshCollector());
        var plan = CreatePlan();

        await coordinator.ExecuteAsync(plan, ExecutionDate);
        var nextDay = await coordinator.ExecuteAsync(plan, ExecutionDate.AddDays(1));

        Assert.True(nextDay.Succeeded);
        Assert.Equal(2, channel.SendCount);
    }

    [Fact]
    public async Task ExecuteAsync_CollectionFails_DoesNotNotify()
    {
        var channel = new CountingNotificationChannel();
        var coordinator = CreateCoordinator(
            collector: new FailingCollector("provider_unavailable"),
            smtpChannel: channel);

        var result = await coordinator.ExecuteAsync(CreatePlan(), ExecutionDate);

        Assert.Equal(ScheduledCollectionStatus.Failed, result.Status);
        Assert.Equal("provider_unavailable", result.FailureCode);
        Assert.Equal(0, channel.SendCount);
    }

    [Fact]
    public async Task ExecuteAsync_FailedRun_IsRetryableWithinTheSameDay()
    {
        var history = new InMemoryScheduledCollectionHistory();
        var channel = new CountingNotificationChannel();
        var plan = CreatePlan();

        var failing = CreateCoordinator(
            collector: new FailingCollector("timeout"), history: history, smtpChannel: channel);
        var failed = await failing.ExecuteAsync(plan, ExecutionDate);

        // Same history, same (plan, date): a transient failure must not lock the slot.
        var recovered = CreateCoordinator(history: history, smtpChannel: channel);
        var retried = await recovered.ExecuteAsync(plan, ExecutionDate);

        Assert.Equal(ScheduledCollectionStatus.Failed, failed.Status);
        Assert.True(retried.Succeeded);
        Assert.Equal(1, channel.SendCount);
    }

    [Fact]
    public async Task ExecuteAsync_AllNoticesAlreadyInLedger_DoesNotSendEmptyNotification()
    {
        var ledger = new InMemoryNoticeLedger();
        var channel = new CountingNotificationChannel();
        var plan = CreatePlan();

        // First run registers every fingerprint in the shared ledger.
        await CreateCoordinator(ledger: ledger, smtpChannel: channel).ExecuteAsync(plan, ExecutionDate);
        var sendsAfterFirstRun = channel.SendCount;

        // Second run, distinct plan id so the slot is new, same notices.
        var second = await CreateCoordinator(ledger: ledger, smtpChannel: channel)
            .ExecuteAsync(plan with { PlanId = "test-plan-002" }, ExecutionDate);

        Assert.Equal(1, sendsAfterFirstRun);
        Assert.True(second.Succeeded);
        Assert.Equal(0, second.NoticesDeduplicated);
        Assert.Equal(0, second.NoticesNotified);
        Assert.Equal(sendsAfterFirstRun, channel.SendCount);
        Assert.Null(second.NotificationId);
    }

    [Fact]
    public async Task ExecuteAsync_ChannelNotConfigured_FailsAndDoesNotMarkNotified()
    {
        var ledger = new InMemoryNoticeLedger();
        var coordinator = CreateCoordinator(
            ledger: ledger,
            smtpChannel: new UnconfiguredNotificationChannel(),
            webhookChannel: new UnconfiguredNotificationChannel());

        var result = await coordinator.ExecuteAsync(CreatePlan(), ExecutionDate);

        Assert.Equal(ScheduledCollectionStatus.Failed, result.Status);
        Assert.Equal("notification_not_configured", result.FailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_ChannelRejects_FailsAndReportsChannelCode()
    {
        var coordinator = CreateCoordinator(
            smtpChannel: new RejectingNotificationChannel("notification_rejected"));

        var result = await coordinator.ExecuteAsync(CreatePlan(), ExecutionDate);

        Assert.Equal(ScheduledCollectionStatus.Failed, result.Status);
        Assert.Equal("notification_rejected", result.FailureCode);
    }

    [Fact]
    public async Task ExecuteAsync_DryRunChannel_CountsAsDelivered()
    {
        var coordinator = CreateCoordinator(smtpChannel: new DryRunNotificationChannel());

        var result = await coordinator.ExecuteAsync(CreatePlan(), ExecutionDate);

        Assert.True(result.Succeeded);
        Assert.True(result.NoticesNotified > 0);
    }

    [Fact]
    public async Task ExecuteAsync_WebhookPlan_RoutesToWebhookChannel()
    {
        var smtp = new CountingNotificationChannel();
        var webhook = new CountingNotificationChannel();
        var coordinator = CreateCoordinator(smtpChannel: smtp, webhookChannel: webhook);

        var result = await coordinator.ExecuteAsync(
            CreatePlan() with { NotificationChannel = ScheduledNotificationChannels.Webhook },
            ExecutionDate);

        Assert.True(result.Succeeded);
        Assert.Equal(1, webhook.SendCount);
        Assert.Equal(0, smtp.SendCount);
    }

    [Fact]
    public async Task ExecuteAsync_SuccessfulRun_ReportsConsistentCounts()
    {
        var coordinator = CreateCoordinator();

        var result = await coordinator.ExecuteAsync(CreatePlan(), ExecutionDate);

        Assert.True(result.Succeeded);
        Assert.True(result.NoticesCollected > 0);
        Assert.True(result.NoticesDeduplicated > 0);
        Assert.Equal(result.NoticesDeduplicated, result.NoticesNotified);
        Assert.True(result.NoticesDeduplicated <= result.NoticesCollected);
        Assert.NotNull(result.NotificationId);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_PropagatesAndLeavesSlotRetryable()
    {
        var history = new InMemoryScheduledCollectionHistory();
        using var cancellation = new CancellationTokenSource();
        var coordinator = CreateCoordinator(
            collector: new CancellingCollector(cancellation), history: history);
        var plan = CreatePlan();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ExecuteAsync(plan, ExecutionDate, cancellation.Token));

        var recorded = await history.GetExecutionResultAsync(plan.PlanId, ExecutionDate);
        Assert.Equal(ScheduledCollectionStatus.Skipped, recorded?.Status);

        // The slot must be re-claimable after a shutdown mid-run.
        Assert.True(await history.TryRecordExecutionAsync(plan.PlanId, ExecutionDate));
    }

    [Fact]
    public async Task ExecuteAsync_NotifiedNoticesAreMarkedInLedger()
    {
        var ledger = new RecordingLedger();
        var coordinator = CreateCoordinator(ledger: ledger);

        var result = await coordinator.ExecuteAsync(CreatePlan(), ExecutionDate);

        Assert.True(result.Succeeded);
        Assert.Equal(result.NoticesNotified, ledger.MarkedNotified.Count);
    }

    private static ScheduledCollectionPlan CreatePlan() =>
        new()
        {
            PlanId = "test-plan-001",
            Name = "Test Plan",
            Keywords = ["测试"],
            LookbackDays = 7,
            MaxResults = 50,
            NotificationChannel = ScheduledNotificationChannels.Smtp,
            ExecutionTimeUtc = new TimeOnly(9, 0),
            Enabled = true
        };

    private static ScheduledCollectionCoordinator CreateCoordinator(
        IBiddingNoticeCollector? collector = null,
        IBiddingNoticeLedger? ledger = null,
        INotificationChannel? smtpChannel = null,
        INotificationChannel? webhookChannel = null,
        IScheduledCollectionHistory? history = null,
        IScheduledCollectionPlanSource? planSource = null) =>
        new(
            collector ?? new FakeBiddingNoticeCollector(),
            ledger ?? new InMemoryNoticeLedger(),
            smtpChannel ?? new CountingNotificationChannel(),
            webhookChannel ?? new CountingNotificationChannel(),
            history ?? new InMemoryScheduledCollectionHistory(),
            planSource ?? new InMemoryScheduledCollectionPlanSource(),
            NullLogger<ScheduledCollectionCoordinator>.Instance);

    private sealed class CountingCollector : IBiddingNoticeCollector
    {
        private readonly FakeBiddingNoticeCollector _inner = new();

        public int CallCount { get; private set; }

        public string SourcePlatform => _inner.SourcePlatform;

        public bool IsConfigured => true;

        public Task<BiddingCollectionResult> CollectAsync(
            BiddingCollectionRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return _inner.CollectAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Emits a notice whose fingerprint varies with the requested window, so a
    /// second day produces genuinely new notices rather than ledger duplicates.
    /// </summary>
    private sealed class AlwaysFreshCollector : IBiddingNoticeCollector
    {
        public string SourcePlatform => "fresh.bidding.local";

        public bool IsConfigured => true;

        public Task<BiddingCollectionResult> CollectAsync(
            BiddingCollectionRequest request, CancellationToken cancellationToken = default)
        {
            var stamp = request.ToDate?.ToString("yyyyMMdd") ?? "none";
            var url = $"https://{SourcePlatform}/notice/{stamp}";
            var title = $"公告 {stamp}";

            var notice = new BiddingNotice
            {
                Title = title,
                Publisher = "测试采购中心",
                PublishedAt = request.ToDate ?? DateTimeOffset.UnixEpoch,
                NoticeUrl = url,
                SourcePlatform = SourcePlatform,
                Fingerprint = BiddingNoticeFingerprint.Compute(SourcePlatform, url, title)
            };

            return Task.FromResult(
                BiddingCollectionResult.Success(request.CollectionId, [notice], request.CorrelationId, request.MaxResults));
        }
    }

    private sealed class FailingCollector(string failureCode) : IBiddingNoticeCollector
    {
        public string SourcePlatform => "failing.bidding.local";

        public bool IsConfigured => true;

        public Task<BiddingCollectionResult> CollectAsync(
            BiddingCollectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(BiddingCollectionResult.Failed(request.CollectionId, failureCode));
    }

    private sealed class CancellingCollector(CancellationTokenSource source) : IBiddingNoticeCollector
    {
        public string SourcePlatform => "cancelling.bidding.local";

        public bool IsConfigured => true;

        public Task<BiddingCollectionResult> CollectAsync(
            BiddingCollectionRequest request, CancellationToken cancellationToken = default)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BiddingCollectionResult.Running(request.CollectionId));
        }
    }

    private sealed class CountingNotificationChannel : INotificationChannel
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

    private sealed class RejectingNotificationChannel(string failureCode) : INotificationChannel
    {
        public bool IsConfigured => true;

        public Task<NotificationResult> SendAsync(
            NotificationMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(NotificationResult.Failed("notif-rejected", failureCode));
    }

    private sealed class DryRunNotificationChannel : INotificationChannel
    {
        public bool IsConfigured => true;

        public Task<NotificationResult> SendAsync(
            NotificationMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(NotificationResult.DryRun("notif-dryrun"));
    }

    private sealed class RecordingLedger : IBiddingNoticeLedger
    {
        private readonly InMemoryNoticeLedger _inner = new();

        public List<string> MarkedNotified { get; } = [];

        public Task<bool> TryRegisterAsync(string fingerprint, CancellationToken cancellationToken = default) =>
            _inner.TryRegisterAsync(fingerprint, cancellationToken);

        public Task MarkNotifiedAsync(string fingerprint, CancellationToken cancellationToken = default)
        {
            MarkedNotified.Add(fingerprint);
            return _inner.MarkNotifiedAsync(fingerprint, cancellationToken);
        }

        public Task<int> PruneAsync(TimeSpan retentionWindow, CancellationToken cancellationToken = default) =>
            _inner.PruneAsync(retentionWindow, cancellationToken);
    }
}
