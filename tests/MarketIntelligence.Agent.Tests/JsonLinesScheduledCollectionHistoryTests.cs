using MarketIntelligence.Agent.Application.Bidding;
using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class JsonLinesScheduledCollectionHistoryTests : IDisposable
{
    private const string PlanId = "plan-001";
    private static readonly DateOnly ExecutionDate = new(2026, 8, 12);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mia-schedule-history-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ClaimingAnUnseenSlot_Succeeds()
    {
        using var history = Create();

        Assert.True(await history.TryRecordExecutionAsync(PlanId, ExecutionDate));
    }

    [Fact]
    public async Task ClaimingAnInFlightSlotAgain_Fails()
    {
        using var history = Create();

        await history.TryRecordExecutionAsync(PlanId, ExecutionDate);

        Assert.False(await history.TryRecordExecutionAsync(PlanId, ExecutionDate));
    }

    [Fact]
    public async Task CompletedSlot_IsNotReclaimable()
    {
        using var history = Create();
        await history.TryRecordExecutionAsync(PlanId, ExecutionDate);
        await history.SaveExecutionResultAsync(
            ScheduledCollectionResult.Success(PlanId, ExecutionDate, 5, 3, 3, "notif-1"));

        Assert.False(await history.TryRecordExecutionAsync(PlanId, ExecutionDate));
    }

    [Fact]
    public async Task FailedSlot_IsReclaimableSameDay()
    {
        using var history = Create();
        await history.TryRecordExecutionAsync(PlanId, ExecutionDate);
        await history.SaveExecutionResultAsync(
            ScheduledCollectionResult.Failed(PlanId, ExecutionDate, "timeout"));

        Assert.True(await history.TryRecordExecutionAsync(PlanId, ExecutionDate));
    }

    [Fact]
    public async Task CompletedSlot_SurvivesRestart()
    {
        using (var first = Create())
        {
            await first.TryRecordExecutionAsync(PlanId, ExecutionDate);
            await first.SaveExecutionResultAsync(
                ScheduledCollectionResult.Success(PlanId, ExecutionDate, 7, 4, 4, "notif-42"));
        }

        // A fresh instance over the same directory stands in for a process restart.
        using var second = Create();

        Assert.False(await second.TryRecordExecutionAsync(PlanId, ExecutionDate));

        var restored = await second.GetExecutionResultAsync(PlanId, ExecutionDate);
        Assert.Equal(ScheduledCollectionStatus.Completed, restored?.Status);
        Assert.Equal("notif-42", restored?.NotificationId);
        Assert.Equal(7, restored?.NoticesCollected);
        Assert.Equal(4, restored?.NoticesNotified);
    }

    [Fact]
    public async Task FailedSlot_SurvivesRestartAndStaysRetryable()
    {
        using (var first = Create())
        {
            await first.TryRecordExecutionAsync(PlanId, ExecutionDate);
            await first.SaveExecutionResultAsync(
                ScheduledCollectionResult.Failed(PlanId, ExecutionDate, "timeout"));
        }

        using var second = Create();

        Assert.Equal("timeout", (await second.GetExecutionResultAsync(PlanId, ExecutionDate))?.FailureCode);
        Assert.True(await second.TryRecordExecutionAsync(PlanId, ExecutionDate));
    }

    [Fact]
    public async Task ReclaimedSlot_DoesNotDuplicateRecordsAcrossRestart()
    {
        using (var first = Create())
        {
            await first.TryRecordExecutionAsync(PlanId, ExecutionDate);
            await first.SaveExecutionResultAsync(
                ScheduledCollectionResult.Failed(PlanId, ExecutionDate, "timeout"));
            await first.TryRecordExecutionAsync(PlanId, ExecutionDate);
            await first.SaveExecutionResultAsync(
                ScheduledCollectionResult.Success(PlanId, ExecutionDate, 2, 2, 2, "notif-final"));
        }

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(_root, "bidding-schedule-history.jsonl"));

        Assert.Single(lines.Where(line => !string.IsNullOrWhiteSpace(line)));

        using var second = Create();
        Assert.Equal("notif-final", (await second.GetExecutionResultAsync(PlanId, ExecutionDate))?.NotificationId);
    }

    [Fact]
    public async Task DistinctDates_AreDistinctSlots()
    {
        using var history = Create();

        await history.TryRecordExecutionAsync(PlanId, ExecutionDate);
        await history.SaveExecutionResultAsync(
            ScheduledCollectionResult.Success(PlanId, ExecutionDate, 1, 1, 1, "notif-day1"));

        Assert.True(await history.TryRecordExecutionAsync(PlanId, ExecutionDate.AddDays(1)));
    }

    [Fact]
    public async Task DistinctPlans_AreDistinctSlots()
    {
        using var history = Create();

        await history.TryRecordExecutionAsync(PlanId, ExecutionDate);

        Assert.True(await history.TryRecordExecutionAsync("plan-002", ExecutionDate));
    }

    [Fact]
    public async Task UnknownSlot_ReturnsNull()
    {
        using var history = Create();

        Assert.Null(await history.GetExecutionResultAsync("never-run", ExecutionDate));
    }

    [Fact]
    public async Task Prune_RemovesOldRecordsAndKeepsRecentOnes()
    {
        using var history = Create();
        var old = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-120));
        var recent = DateOnly.FromDateTime(DateTime.UtcNow);

        await history.TryRecordExecutionAsync(PlanId, old);
        await history.SaveExecutionResultAsync(
            ScheduledCollectionResult.Success(PlanId, old, 1, 1, 1, "notif-old"));
        await history.TryRecordExecutionAsync(PlanId, recent);
        await history.SaveExecutionResultAsync(
            ScheduledCollectionResult.Success(PlanId, recent, 1, 1, 1, "notif-recent"));

        await history.PruneAsync(TimeSpan.FromDays(90));

        Assert.Null(await history.GetExecutionResultAsync(PlanId, old));
        Assert.NotNull(await history.GetExecutionResultAsync(PlanId, recent));
    }

    [Fact]
    public async Task CorruptedFile_IsIsolatedAndDoesNotSuppressAFuturePush()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bidding-schedule-history.jsonl");
        await File.WriteAllTextAsync(path, "{ this is not valid json\n");

        using var history = Create();

        // Fails open: a slot that cannot be proven completed must remain claimable,
        // otherwise a corrupt file would silently suppress a push that never happened.
        Assert.True(await history.TryRecordExecutionAsync(PlanId, ExecutionDate));
        Assert.NotEmpty(Directory.GetFiles(_root, "*.corrupted.*"));
    }

    [Fact]
    public void MissingLedgerRoot_FailsFast()
    {
        var options = Options.Create(new BiddingOptions { LedgerRoot = null });

        Assert.Throws<InvalidOperationException>(() => new JsonLinesScheduledCollectionHistory(
            options, NullLogger<JsonLinesScheduledCollectionHistory>.Instance));
    }

    private JsonLinesScheduledCollectionHistory Create() =>
        new(
            Options.Create(new BiddingOptions { LedgerRoot = _root }),
            NullLogger<JsonLinesScheduledCollectionHistory>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
