namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Orchestrates scheduled bidding notice collection: evaluates plans, triggers
/// collection, dedupes via ledger, renders notifications, and delivers via channels.
/// </summary>
public interface IScheduledCollectionCoordinator
{
    /// <summary>
    /// Execute a scheduled collection plan once. Idempotent by (planId, executionDate):
    /// if the same plan executed today already, returns the cached result without
    /// re-collecting or re-notifying.
    /// </summary>
    Task<ScheduledCollectionResult> ExecuteAsync(
        ScheduledCollectionPlan plan,
        DateOnly executionDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates all registered plans against the current time and executes
    /// those due to run. Used by the background worker.
    /// </summary>
    Task<IReadOnlyList<ScheduledCollectionResult>> ExecuteDuePlansAsync(
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default);
}

public sealed record ScheduledCollectionResult
{
    public required string PlanId { get; init; }
    public required DateOnly ExecutionDate { get; init; }
    public required ScheduledCollectionStatus Status { get; init; }

    /// <summary>Notices returned by the collector, before ledger dedupe.</summary>
    public int NoticesCollected { get; init; }

    /// <summary>Notices that survived ledger dedupe, i.e. genuinely new ones.</summary>
    public int NoticesDeduplicated { get; init; }

    /// <summary>Notices actually included in a delivered notification.</summary>
    public int NoticesNotified { get; init; }

    public string? FailureCode { get; init; }
    public string? NotificationId { get; init; }

    public bool Succeeded => Status == ScheduledCollectionStatus.Completed;

    /// <summary>
    /// Claim marker written when a run starts, so a crash mid-run leaves a
    /// distinguishable state rather than looking like a completed push.
    /// </summary>
    public static ScheduledCollectionResult Running(string planId, DateOnly executionDate) =>
        new()
        {
            PlanId = planId,
            ExecutionDate = executionDate,
            Status = ScheduledCollectionStatus.Running
        };

    public static ScheduledCollectionResult Success(
        string planId,
        DateOnly executionDate,
        int collected,
        int deduplicated,
        int notified,
        string? notificationId = null) =>
        new()
        {
            PlanId = planId,
            ExecutionDate = executionDate,
            Status = ScheduledCollectionStatus.Completed,
            NoticesCollected = collected,
            NoticesDeduplicated = deduplicated,
            NoticesNotified = notified,
            NotificationId = notificationId
        };

    public static ScheduledCollectionResult Failed(
        string planId,
        DateOnly executionDate,
        string failureCode) =>
        new()
        {
            PlanId = planId,
            ExecutionDate = executionDate,
            Status = ScheduledCollectionStatus.Failed,
            FailureCode = failureCode
        };

    public static ScheduledCollectionResult Skipped(
        string planId,
        DateOnly executionDate,
        string reason) =>
        new()
        {
            PlanId = planId,
            ExecutionDate = executionDate,
            Status = ScheduledCollectionStatus.Skipped,
            FailureCode = reason
        };
}

public enum ScheduledCollectionStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}
