namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// In-memory ledger used for testing and environments where restart-persistence
/// is not required. All state is lost when the process exits.
/// </summary>
public sealed class InMemoryNoticeLedger : IBiddingNoticeLedger
{
    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Constructs a ledger using the system clock.
    /// </summary>
    public InMemoryNoticeLedger()
        : this(static () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Constructs a ledger with an injected clock. Retention is a time-based
    /// policy, so tests need to control time directly rather than sleeping and
    /// hoping the wall clock cooperates.
    /// </summary>
    public InMemoryNoticeLedger(Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<bool> TryRegisterAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.ContainsKey(fingerprint))
            {
                return false;
            }

            _entries[fingerprint] = new LedgerEntry
            {
                Fingerprint = fingerprint,
                FirstSeenAt = _clock(),
                NotifiedAt = null
            };

            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkNotifiedAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(fingerprint, out var entry))
            {
                entry.NotifiedAt = _clock();
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> PruneAsync(TimeSpan retentionWindow, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = _clock() - retentionWindow;
            var toRemove = _entries
                .Where(pair => LastActivity(pair.Value) < cutoff)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _entries.Remove(key);
            }

            return toRemove.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static DateTimeOffset LastActivity(LedgerEntry entry) =>
        entry.NotifiedAt ?? entry.FirstSeenAt;

    private sealed class LedgerEntry
    {
        public required string Fingerprint { get; init; }
        public required DateTimeOffset FirstSeenAt { get; init; }
        public DateTimeOffset? NotifiedAt { get; set; }
    }
}
