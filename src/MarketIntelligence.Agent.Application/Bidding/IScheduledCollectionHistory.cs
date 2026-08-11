namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Tracks execution history for scheduled collection plans to ensure
/// idempotency: (planId, executionDate) should only produce one notification.
/// </summary>
public interface IScheduledCollectionHistory
{
    /// <summary>
    /// Claims the (planId, executionDate) slot for execution, writing a
    /// <see cref="ScheduledCollectionStatus.Running"/> marker.
    /// </summary>
    /// <returns>
    /// True when the slot was claimed. False when the slot is already claimed by an
    /// in-flight run or was completed, which is what makes the push idempotent per
    /// (plan, date). A previously <see cref="ScheduledCollectionStatus.Failed"/> or
    /// <see cref="ScheduledCollectionStatus.Skipped"/> slot is re-claimable, so a
    /// transient collector or channel failure can be retried within the same day
    /// instead of being locked out until tomorrow.
    /// </returns>
    Task<bool> TryRecordExecutionAsync(
        string planId,
        DateOnly executionDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve the result of a previous execution, if any.
    /// </summary>
    Task<ScheduledCollectionResult?> GetExecutionResultAsync(
        string planId,
        DateOnly executionDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Save the result of an execution.
    /// </summary>
    Task SaveExecutionResultAsync(
        ScheduledCollectionResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove execution records older than the retention period.
    /// </summary>
    Task PruneAsync(
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default);
}
