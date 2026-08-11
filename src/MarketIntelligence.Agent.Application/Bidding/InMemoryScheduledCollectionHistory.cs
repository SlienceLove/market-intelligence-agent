namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// In-memory implementation of IScheduledCollectionHistory for testing and
/// single-instance deployments. Not suitable for distributed scenarios.
/// </summary>
public sealed class InMemoryScheduledCollectionHistory : IScheduledCollectionHistory
{
    private readonly Dictionary<HistoryKey, ScheduledCollectionResult> _history = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<bool> TryRecordExecutionAsync(
        string planId,
        DateOnly executionDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        var key = new HistoryKey(planId, executionDate);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_history.TryGetValue(key, out var existing) && !IsReclaimable(existing.Status))
            {
                return false;
            }

            _history[key] = ScheduledCollectionResult.Running(planId, executionDate);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ScheduledCollectionResult?> GetExecutionResultAsync(
        string planId,
        DateOnly executionDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);

        var key = new HistoryKey(planId, executionDate);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _history.TryGetValue(key, out var result) ? result : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveExecutionResultAsync(
        ScheduledCollectionResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var key = new HistoryKey(result.PlanId, result.ExecutionDate);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _history[key] = result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task PruneAsync(
        TimeSpan retentionPeriod,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateOnly.FromDateTime(DateTime.UtcNow.Subtract(retentionPeriod));

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var expiredKeys = _history.Keys
                .Where(k => k.ExecutionDate < cutoffDate)
                .ToArray();

            foreach (var key in expiredKeys)
            {
                _history.Remove(key);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A completed slot is never re-run: that is the idempotency guarantee. A
    /// Running slot is held by an in-flight run. Failed and Skipped slots are
    /// re-claimable so a retry within the same day is possible.
    /// </summary>
    private static bool IsReclaimable(ScheduledCollectionStatus status) =>
        status is ScheduledCollectionStatus.Failed or ScheduledCollectionStatus.Skipped;

    private readonly record struct HistoryKey(string PlanId, DateOnly ExecutionDate);
}
