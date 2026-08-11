using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// JSON Lines persistent ledger, one entry per line, appended on register and
/// compacted periodically. The file lives in a controlled directory so a caller
/// cannot redirect writes to an arbitrary location.
/// </summary>
public sealed class JsonLinesBiddingNoticeLedger : IBiddingNoticeLedger, IDisposable
{
    private const string LedgerFileName = "bidding-notices.jsonl";
    private const string CorruptedSuffix = ".corrupted";

    private readonly string _ledgerPath;
    private readonly Dictionary<string, LedgerEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonLinesBiddingNoticeLedger> _logger;
    private bool _disposed;

    public JsonLinesBiddingNoticeLedger(
        IOptions<BiddingOptions> options,
        ILogger<JsonLinesBiddingNoticeLedger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var ledgerRoot = options.Value.LedgerRoot;
        if (string.IsNullOrWhiteSpace(ledgerRoot))
        {
            throw new InvalidOperationException(
                "BiddingOptions.LedgerRoot is not configured. The ledger cannot determine where to persist entries.");
        }

        if (!Directory.Exists(ledgerRoot))
        {
            Directory.CreateDirectory(ledgerRoot);
        }

        _ledgerPath = Path.Combine(ledgerRoot, LedgerFileName);
        Load();
    }

    public async Task<bool> TryRegisterAsync(string fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.ContainsKey(fingerprint))
            {
                return false;
            }

            var entry = new LedgerEntry
            {
                Fingerprint = fingerprint,
                FirstSeenAt = DateTimeOffset.UtcNow,
                NotifiedAt = null
            };

            _entries[fingerprint] = entry;
            await AppendEntryAsync(entry, cancellationToken).ConfigureAwait(false);
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
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(fingerprint, out var entry))
            {
                entry.NotifiedAt = DateTimeOffset.UtcNow;
                await CompactAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<int> PruneAsync(TimeSpan retentionWindow, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cutoff = DateTimeOffset.UtcNow - retentionWindow;
            var toRemove = _entries
                .Where(pair => LastActivity(pair.Value) < cutoff)
                .Select(pair => pair.Key)
                .ToList();

            foreach (var key in toRemove)
            {
                _entries.Remove(key);
            }

            if (toRemove.Count > 0)
            {
                await CompactAsync(cancellationToken).ConfigureAwait(false);
            }

            return toRemove.Count;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lock.Dispose();
        _disposed = true;
    }

    private static DateTimeOffset LastActivity(LedgerEntry entry) =>
        entry.NotifiedAt ?? entry.FirstSeenAt;

    private void Load()
    {
        if (!File.Exists(_ledgerPath))
        {
            _logger.LogInformation("Ledger file {Path} does not exist; starting with an empty ledger.", _ledgerPath);
            return;
        }

        try
        {
            var lines = File.ReadAllLines(_ledgerPath, Encoding.UTF8);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var entry = JsonSerializer.Deserialize<LedgerEntry>(line);
                if (entry is not null && !string.IsNullOrWhiteSpace(entry.Fingerprint))
                {
                    _entries[entry.Fingerprint] = entry;
                }
            }

            _logger.LogInformation("Loaded {Count} entries from ledger {Path}.", _entries.Count, _ledgerPath);
        }
        catch (Exception exception)
        {
            var corruptedPath = _ledgerPath + CorruptedSuffix + $".{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
            _logger.LogError(exception, "Ledger file {Path} is corrupted. Moving to {CorruptedPath} and starting fresh.", _ledgerPath, corruptedPath);

            try
            {
                File.Move(_ledgerPath, corruptedPath);
            }
            catch (Exception moveException)
            {
                _logger.LogError(moveException, "Failed to move corrupted ledger to {CorruptedPath}.", corruptedPath);
                throw new InvalidOperationException(
                    $"Ledger file {_ledgerPath} is corrupted and could not be isolated. Manual intervention required.",
                    exception);
            }
        }
    }

    private async Task AppendEntryAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(entry) + "\n";
        await File.AppendAllTextAsync(_ledgerPath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompactAsync(CancellationToken cancellationToken)
    {
        var tempPath = _ledgerPath + ".tmp";
        await using (var writer = new StreamWriter(tempPath, append: false, Encoding.UTF8))
        {
            foreach (var entry in _entries.Values)
            {
                var line = JsonSerializer.Serialize(entry);
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        File.Replace(tempPath, _ledgerPath, null);
    }

    private sealed class LedgerEntry
    {
        [JsonPropertyName("fingerprint")]
        public required string Fingerprint { get; init; }

        [JsonPropertyName("firstSeenAt")]
        public required DateTimeOffset FirstSeenAt { get; init; }

        [JsonPropertyName("notifiedAt")]
        public DateTimeOffset? NotifiedAt { get; set; }
    }
}
