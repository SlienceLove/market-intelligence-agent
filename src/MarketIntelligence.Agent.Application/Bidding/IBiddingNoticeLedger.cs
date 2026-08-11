namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Persistent ledger tracking notices that have been pushed, so restarts and
/// scheduled runs never re-push the same notice to a recipient.
/// </summary>
/// <remarks>
/// A ledger entry is keyed by notice fingerprint and records first-seen time,
/// notification status, and an optional last-notified timestamp. Entries older
/// than the retention window are pruned so the ledger does not grow without bound.
/// </remarks>
public interface IBiddingNoticeLedger
{
    /// <summary>
    /// Registers a notice in the ledger if it has never been seen before.
    /// </summary>
    /// <returns>
    /// True when the fingerprint is novel and was registered; false when it was
    /// already present and the call had no effect. A false return means the
    /// notice should be suppressed to prevent duplicate push.
    /// </returns>
    Task<bool> TryRegisterAsync(string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a registered notice as notified, recording the timestamp so the
    /// retention window can be measured from last activity rather than first seen.
    /// Idempotent: calling this on an already-notified fingerprint updates the
    /// timestamp but is not an error.
    /// </summary>
    Task MarkNotifiedAsync(string fingerprint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes entries whose last activity (notified or first-seen, whichever is
    /// later) is older than <paramref name="retentionWindow"/>. Returns the count
    /// of entries pruned. Call this periodically to bound ledger size.
    /// </summary>
    Task<int> PruneAsync(TimeSpan retentionWindow, CancellationToken cancellationToken = default);
}
