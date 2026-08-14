using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Application.Bidding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Loads scheduled collection plans from a controlled JSON file and preserves a
/// last-known-good snapshot. Invalid edits never replace the active in-memory
/// plans; after a restart the snapshot provides the same rollback behavior.
/// </summary>
public sealed class JsonFileScheduledCollectionPlanSource : IScheduledCollectionPlanSource, IDisposable
{
    public const string PlanFileName = "scheduled-plans.json";
    public const string LastKnownGoodFileName = "scheduled-plans.last-known-good.json";
    public const string AuditFileName = "scheduled-plans.audit.jsonl";
    public const string PreviousAuditFileName = "scheduled-plans.audit.previous.jsonl";

    private const int MaximumPlanCount = 100;
    private const int MaximumPlanFileBytes = 1_048_576;
    private const int MaximumAuditFileBytes = 1_048_576;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static readonly JsonSerializerOptions AuditOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _planPath;
    private readonly string _lastKnownGoodPath;
    private readonly string _auditPath;
    private readonly string _previousAuditPath;
    private readonly ILogger<JsonFileScheduledCollectionPlanSource> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Queue<string> _pendingAuditLines = new();

    private IReadOnlyList<ScheduledCollectionPlan> _currentPlans = [];
    private string? _lastObservation;
    private bool _hasValidPlans;
    private bool _disposed;

    public JsonFileScheduledCollectionPlanSource(
        IOptions<BiddingOptions> options,
        ILogger<JsonFileScheduledCollectionPlanSource> logger)
        : this(options, logger, TimeProvider.System)
    {
    }

    internal JsonFileScheduledCollectionPlanSource(
        IOptions<BiddingOptions> options,
        ILogger<JsonFileScheduledCollectionPlanSource> logger,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        if (string.IsNullOrWhiteSpace(options.Value.PlanRoot))
        {
            throw new InvalidOperationException(
                "BiddingOptions.PlanRoot is not configured. The plan source cannot determine where to load plans.");
        }

        var root = Path.GetFullPath(options.Value.PlanRoot);
        Directory.CreateDirectory(root);
        _planPath = Path.Combine(root, PlanFileName);
        _lastKnownGoodPath = Path.Combine(root, LastKnownGoodFileName);
        _auditPath = Path.Combine(root, AuditFileName);
        _previousAuditPath = Path.Combine(root, PreviousAuditFileName);
    }

    public async Task<IReadOnlyList<ScheduledCollectionPlan>> GetPlansAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushPendingAuditsAsync(cancellationToken).ConfigureAwait(false);

            if (File.Exists(_planPath))
            {
                string? fingerprint = null;
                try
                {
                    var activeBytes = await ReadBoundedFileAsync(_planPath, cancellationToken)
                        .ConfigureAwait(false);
                    fingerprint = ComputeFingerprint(activeBytes);
                    var observation = $"active:{fingerprint}";

                    if (string.Equals(_lastObservation, observation, StringComparison.Ordinal))
                    {
                        return _currentPlans;
                    }

                    var plans = ParseAndValidate(activeBytes);
                    await PersistLastKnownGoodAsync(activeBytes, cancellationToken).ConfigureAwait(false);
                    _currentPlans = plans;
                    _hasValidPlans = true;
                    _lastObservation = observation;
                    await TryAppendAuditAsync("loaded", "active", fingerprint, plans.Count, null, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogInformation(
                        "Loaded {Count} scheduled collection plans from the controlled plan file.",
                        plans.Count);
                    return _currentPlans;
                }
                catch (Exception exception) when (IsPlanLoadFailure(exception))
                {
                    _lastObservation = exception is JsonException or InvalidDataException && fingerprint is not null
                        ? $"active:{fingerprint}"
                        : null;
                    await TryAppendAuditAsync(
                            "rejected",
                            "active",
                            fingerprint,
                            _currentPlans.Count,
                            ClassifyFailure(exception),
                            cancellationToken)
                        .ConfigureAwait(false);
                    _logger.LogError(
                        exception,
                        "Rejected the active scheduled plan file; retaining the last-known-good plans.");
                }
            }
            else
            {
                const string observation = "active:missing";
                if (string.Equals(_lastObservation, observation, StringComparison.Ordinal))
                {
                    return _currentPlans;
                }

                _lastObservation = observation;
                await TryAppendAuditAsync("missing", "active", null, _currentPlans.Count, "plan_file_missing", cancellationToken)
                    .ConfigureAwait(false);
            }

            if (_hasValidPlans)
            {
                return _currentPlans;
            }

            return await LoadLastKnownGoodAsync(cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<ScheduledCollectionPlan>> LoadLastKnownGoodAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_lastKnownGoodPath))
        {
            _currentPlans = [];
            return _currentPlans;
        }

        try
        {
            var bytes = await ReadBoundedFileAsync(_lastKnownGoodPath, cancellationToken)
                .ConfigureAwait(false);
            var plans = ParseAndValidate(bytes);
            var fingerprint = ComputeFingerprint(bytes);
            _currentPlans = plans;
            _hasValidPlans = true;
            await TryAppendAuditAsync("fallback", "last-known-good", fingerprint, plans.Count, null, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "Using {Count} plans from the last-known-good snapshot.", plans.Count);
            return _currentPlans;
        }
        catch (Exception exception) when (IsPlanLoadFailure(exception))
        {
            _currentPlans = [];
            await TryAppendAuditAsync(
                    "rejected",
                    "last-known-good",
                    null,
                    0,
                    ClassifyFailure(exception),
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.LogError(
                exception,
                "The last-known-good plan snapshot is invalid; scheduling remains disabled.");
            return _currentPlans;
        }
    }

    private static IReadOnlyList<ScheduledCollectionPlan> ParseAndValidate(byte[] content)
    {
        using (var jsonDocument = JsonDocument.Parse(content, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        }))
        {
            RejectDuplicateProperties(jsonDocument.RootElement);
        }

        var document = JsonSerializer.Deserialize<PlanFileDocument>(content, ReadOptions)
            ?? throw new InvalidDataException("The plan document is empty.");

        if (document.Version != 1)
        {
            throw new InvalidDataException("Unsupported plan document version.");
        }

        if (document.Plans is null)
        {
            throw new InvalidDataException("The plan list cannot be null.");
        }

        if (document.Plans.Count > MaximumPlanCount)
        {
            throw new InvalidDataException("The plan count exceeds the configured safety limit.");
        }

        var plans = new List<ScheduledCollectionPlan>(document.Plans.Count);
        var planIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in document.Plans)
        {
            if (entry is null)
            {
                throw new InvalidDataException("The plan list cannot contain null entries.");
            }

            if (entry.Keywords is null || entry.DaysOfWeek is null || entry.ExecutionTimeUtc is null)
            {
                throw new InvalidDataException("A plan is missing a required collection or schedule field.");
            }

            if (entry.DaysOfWeek.Any(day => !Enum.IsDefined(day)))
            {
                throw new InvalidDataException("A plan contains an invalid day of week.");
            }

            var plan = new ScheduledCollectionPlan
            {
                PlanId = entry.PlanId ?? string.Empty,
                Name = entry.Name ?? string.Empty,
                Keywords = entry.Keywords,
                RegionFilter = entry.RegionFilter,
                IndustryFilter = entry.IndustryFilter,
                LookbackDays = entry.LookbackDays,
                MaxResults = entry.MaxResults,
                NotificationChannel = entry.NotificationChannel ?? string.Empty,
                ExecutionTimeUtc = entry.ExecutionTimeUtc.Value,
                DaysOfWeek = entry.DaysOfWeek.ToHashSet(),
                Enabled = entry.Enabled
            };

            var failure = plan.Validate();
            if (failure is not null)
            {
                throw new InvalidDataException($"Plan validation failed: {failure}.");
            }

            if (!planIds.Add(plan.PlanId))
            {
                throw new InvalidDataException("Duplicate plan identifiers are not allowed.");
            }

            plans.Add(plan);
        }

        return plans;
    }

    private async Task PersistLastKnownGoodAsync(byte[] content, CancellationToken cancellationToken)
    {
        var temporaryPath = $"{_lastKnownGoodPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _lastKnownGoodPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumPlanFileBytes)
        {
            throw new InvalidDataException("The plan file exceeds the one-megabyte safety limit.");
        }

        var boundedBuffer = new byte[MaximumPlanFileBytes + 1];
        var bytesRead = 0;
        while (bytesRead < boundedBuffer.Length)
        {
            var count = await stream.ReadAsync(
                    boundedBuffer.AsMemory(bytesRead, boundedBuffer.Length - bytesRead),
                    cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            bytesRead += count;
        }

        if (bytesRead > MaximumPlanFileBytes)
        {
            throw new InvalidDataException("The plan file exceeds the one-megabyte safety limit.");
        }

        return boundedBuffer[..bytesRead];
    }

    private async Task TryAppendAuditAsync(
        string outcome,
        string source,
        string? fingerprint,
        int planCount,
        string? failureCode,
        CancellationToken cancellationToken)
    {
        var entry = new PlanAuditEntry(
            _timeProvider.GetUtcNow(),
            outcome,
            source,
            fingerprint,
            planCount,
            failureCode);
        var line = JsonSerializer.Serialize(entry, AuditOptions) + Environment.NewLine;
        if (!await TryWriteAuditLineAsync(line, cancellationToken).ConfigureAwait(false) &&
            _pendingAuditLines.Count < MaximumPlanCount)
        {
            _pendingAuditLines.Enqueue(line);
        }
    }

    private async Task FlushPendingAuditsAsync(CancellationToken cancellationToken)
    {
        while (_pendingAuditLines.TryPeek(out var line))
        {
            if (!await TryWriteAuditLineAsync(line, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            _pendingAuditLines.Dequeue();
        }
    }

    private async Task<bool> TryWriteAuditLineAsync(
        string line,
        CancellationToken cancellationToken)
    {
        try
        {
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (File.Exists(_auditPath) &&
                new FileInfo(_auditPath).Length + lineBytes > MaximumAuditFileBytes)
            {
                File.Move(_auditPath, _previousAuditPath, overwrite: true);
            }

            await File.AppendAllTextAsync(_auditPath, line, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Could not append the scheduled-plan audit entry; plan availability is unchanged.");
            return false;
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var propertyNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!propertyNames.Add(property.Name))
                {
                    throw new InvalidDataException("The plan document contains a duplicate JSON property.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }

    private static string ComputeFingerprint(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsPlanLoadFailure(Exception exception) =>
        exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static string ClassifyFailure(Exception exception) => exception switch
    {
        JsonException => "invalid_plan_json",
        InvalidDataException => "invalid_plan_document",
        UnauthorizedAccessException => "plan_file_access_denied",
        IOException => "plan_file_io_error",
        _ => "invalid_plan_document"
    };

    private sealed record PlanFileDocument
    {
        [JsonPropertyName("version")]
        public int Version { get; init; }

        [JsonPropertyName("plans")]
        public List<PlanFileEntry?>? Plans { get; init; }
    }

    private sealed record PlanFileEntry
    {
        [JsonPropertyName("planId")]
        public string? PlanId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("keywords")]
        public List<string>? Keywords { get; init; }

        [JsonPropertyName("regionFilter")]
        public string? RegionFilter { get; init; }

        [JsonPropertyName("industryFilter")]
        public string? IndustryFilter { get; init; }

        [JsonPropertyName("lookbackDays")]
        public int LookbackDays { get; init; } = 1;

        [JsonPropertyName("maxResults")]
        public int MaxResults { get; init; } = 50;

        [JsonPropertyName("notificationChannel")]
        public string? NotificationChannel { get; init; }

        [JsonPropertyName("executionTimeUtc")]
        public TimeOnly? ExecutionTimeUtc { get; init; }

        [JsonPropertyName("daysOfWeek")]
        public List<DayOfWeek>? DaysOfWeek { get; init; }

        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; } = true;
    }

    private sealed record PlanAuditEntry(
        DateTimeOffset TimestampUtc,
        string Outcome,
        string Source,
        string? Fingerprint,
        int PlanCount,
        string? FailureCode);
}
