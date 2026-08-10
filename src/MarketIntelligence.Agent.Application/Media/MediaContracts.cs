using System.Collections.ObjectModel;

namespace MarketIntelligence.Agent.Application.Media;

public enum MediaJobKind
{
    Collection,
    Transcription,
    FrameOcr,
    SpeechSynthesis,
    VideoComposition
}

/// <summary>
/// The durable state vocabulary shared by API, application, and worker code.
/// Explicit values preserve the numeric values of the original terminal states.
/// </summary>
public enum MediaJobStatus
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
    Accepted = 3,
    Running = 4
}

public enum MediaFailureCategory
{
    None,
    Validation,
    Security,
    Authorization,
    Unsupported,
    LimitExceeded,
    Conflict,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    Transient,
    EmptyResult,
    Cancelled,
    Internal,
    Unknown
}

public static class MediaContractLimits
{
    public const int MaxJobIdCharacters = 128;
    public const int MaxCorrelationIdCharacters = 128;
    public const int MaxIdempotencyKeyCharacters = 256;
    public const int MaxInputAssets = 32;
    public const int MaxParameters = 64;
    public const int MaxParameterKeyCharacters = 64;
    public const int MaxParameterValueCharacters = 2_048;
    public const int MaxAssetUriCharacters = 2_048;
    public const int MaxMediaTypeCharacters = 128;
    public const int MaxTimedTextCharacters = 10_000;
    public const int MaxFailureCodeCharacters = 64;
    public const int MaxFailureMessageCharacters = 512;
}

public static class MediaFailureCatalog
{
    private static readonly IReadOnlyDictionary<string, MediaFailureCategory> Categories =
        new ReadOnlyDictionary<string, MediaFailureCategory>(new Dictionary<string, MediaFailureCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["job_id_required"] = MediaFailureCategory.Validation,
            ["invalid_job_id"] = MediaFailureCategory.Validation,
            ["unsupported_media_job"] = MediaFailureCategory.Unsupported,
            ["input_asset_required"] = MediaFailureCategory.Validation,
            ["invalid_input_asset"] = MediaFailureCategory.Validation,
            ["invalid_asset_uri"] = MediaFailureCategory.Validation,
            ["unsafe_asset_reference"] = MediaFailureCategory.Security,
            ["invalid_asset_media_type"] = MediaFailureCategory.Validation,
            ["invalid_asset_size"] = MediaFailureCategory.Validation,
            ["invalid_asset_duration"] = MediaFailureCategory.Validation,
            ["input_asset_limit_exceeded"] = MediaFailureCategory.LimitExceeded,
            ["invalid_correlation_id"] = MediaFailureCategory.Validation,
            ["invalid_idempotency_key"] = MediaFailureCategory.Validation,
            ["parameters_limit_exceeded"] = MediaFailureCategory.LimitExceeded,
            ["invalid_parameter"] = MediaFailureCategory.Validation,
            ["parameter_limit_exceeded"] = MediaFailureCategory.LimitExceeded,
            ["invalid_status"] = MediaFailureCategory.Validation,
            ["invalid_status_transition"] = MediaFailureCategory.Conflict,
            ["failure_code_required"] = MediaFailureCategory.Validation,
            ["invalid_failure_payload"] = MediaFailureCategory.Validation,
            ["invalid_request"] = MediaFailureCategory.Validation,
            ["job_conflict"] = MediaFailureCategory.Conflict,
            ["queue_unavailable"] = MediaFailureCategory.ProviderUnavailable,
            ["internal_error"] = MediaFailureCategory.Internal,
            ["invalid_output_asset"] = MediaFailureCategory.Validation,
            ["invalid_timed_text"] = MediaFailureCategory.Validation,
            ["timed_text_limit_exceeded"] = MediaFailureCategory.LimitExceeded,
            ["invalid_confidence"] = MediaFailureCategory.Validation,
            ["unsupported_source_uri"] = MediaFailureCategory.Security,
            ["private_source_uri"] = MediaFailureCategory.Security,
            ["source_host_not_allowed"] = MediaFailureCategory.Authorization,
            ["provider_not_configured"] = MediaFailureCategory.ProviderUnavailable,
            ["provider_unavailable"] = MediaFailureCategory.ProviderUnavailable,
            ["unauthorized"] = MediaFailureCategory.Authorization,
            ["forbidden"] = MediaFailureCategory.Authorization,
            ["rate_limited"] = MediaFailureCategory.RateLimited,
            ["timeout"] = MediaFailureCategory.Timeout,
            ["transient_provider_failure"] = MediaFailureCategory.Transient,
            ["empty_transcript"] = MediaFailureCategory.EmptyResult,
            ["empty_ocr_result"] = MediaFailureCategory.EmptyResult,
            ["invalid_ocr_result"] = MediaFailureCategory.Validation,
            ["cancelled"] = MediaFailureCategory.Cancelled,
            ["unsupported_audio_format"] = MediaFailureCategory.Unsupported,
            ["audio_size_exceeded"] = MediaFailureCategory.LimitExceeded,
            ["audio_duration_exceeded"] = MediaFailureCategory.LimitExceeded,
            ["composition_av_drift"] = MediaFailureCategory.Validation,
            ["composition_output_missing"] = MediaFailureCategory.Internal,
            ["composition_output_too_large"] = MediaFailureCategory.LimitExceeded,
            ["frame_sampling_failed"] = MediaFailureCategory.Internal,
            ["empty_frame_sampling_result"] = MediaFailureCategory.EmptyResult,
            ["media_probe_failed"] = MediaFailureCategory.Internal
        });

    public static MediaFailureCategory Classify(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return MediaFailureCategory.None;
        }

        var normalized = failureCode.Trim();
        if (Categories.TryGetValue(normalized, out var category))
        {
            return category;
        }

        if (normalized.StartsWith("unsupported_", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.Unsupported;
        }

        if (normalized.StartsWith("invalid_", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("_required", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.Validation;
        }

        if (normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.Timeout;
        }

        if (normalized.Contains("rate", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.RateLimited;
        }

        if (normalized.Contains("conflict", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.Conflict;
        }

        if (normalized.StartsWith("provider_", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.ProviderUnavailable;
        }

        if (normalized.EndsWith("_exceeded", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            return MediaFailureCategory.LimitExceeded;
        }

        return MediaFailureCategory.Unknown;
    }

    public static bool IsRetryable(string? failureCode)
    {
        if (string.Equals(failureCode?.Trim(), "provider_not_configured", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Classify(failureCode) is MediaFailureCategory.RateLimited or
            MediaFailureCategory.Timeout or
            MediaFailureCategory.ProviderUnavailable or
            MediaFailureCategory.Transient;
    }

    /// <summary>
    /// Keeps provider messages useful while preventing credentials, paths, URLs,
    /// and control characters from crossing the application boundary.
    /// </summary>
    public static string? SanitizeMessage(string? failureCode, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = message.Trim();
        if (normalized.Any(char.IsControl) ||
            normalized.Contains("://", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains('\\'))
        {
            return SafeDefaultMessage(failureCode);
        }

        return normalized.Length <= MediaContractLimits.MaxFailureMessageCharacters
            ? normalized
            : normalized[..MediaContractLimits.MaxFailureMessageCharacters];
    }

    public static string SafeDefaultMessage(string? failureCode) =>
        string.IsNullOrWhiteSpace(failureCode)
            ? "The media operation failed."
            : "The media operation failed.";
}

public sealed record MediaAssetReference(
    string Uri,
    string MediaType,
    long? SizeBytes = null,
    TimeSpan? Duration = null)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Uri) || Uri.Length > MediaContractLimits.MaxAssetUriCharacters)
        {
            return "invalid_asset_uri";
        }

        if (Uri.Any(char.IsControl) || Uri.Contains('\\'))
        {
            return "invalid_asset_uri";
        }

        if (System.Uri.TryCreate(Uri, UriKind.Absolute, out var parsed))
        {
            if (!string.IsNullOrEmpty(parsed.UserInfo))
            {
                return "private_source_uri";
            }

            if (parsed.Scheme is "file")
            {
                // Keep the source-policy error vocabulary for collectors. The
                // source URI policy rejects local files before any I/O occurs.
                return "unsupported_source_uri";
            }

            if (parsed.Scheme is "data" or "javascript")
            {
                return "unsafe_asset_reference";
            }

            if (parsed.Query.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                parsed.Query.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                parsed.Query.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                parsed.Query.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
                parsed.Query.Contains("apikey", StringComparison.OrdinalIgnoreCase))
            {
                return "unsafe_asset_reference";
            }

            if (string.IsNullOrWhiteSpace(parsed.Host) &&
                parsed.Scheme is not "asset" and not "temp")
            {
                return "invalid_asset_uri";
            }
        }
        else if (Uri.StartsWith("/", StringComparison.Ordinal) ||
                 Uri.StartsWith("//", StringComparison.Ordinal))
        {
            return "unsafe_asset_reference";
        }

        if (string.IsNullOrWhiteSpace(MediaType) ||
            MediaType.Length > MediaContractLimits.MaxMediaTypeCharacters ||
            MediaType.Any(char.IsControl) ||
            MediaType.Any(char.IsWhiteSpace) ||
            MediaType.Count(value => value == '/') != 1)
        {
            return "invalid_asset_media_type";
        }

        if (SizeBytes is < 0)
        {
            return "invalid_asset_size";
        }

        if (Duration is not null && Duration.Value < TimeSpan.Zero)
        {
            return "invalid_asset_duration";
        }

        return null;
    }
}

public sealed record TimedTextSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    double? Confidence = null)
{
    public bool IsValid => Validate() is null;

    public string? Validate(int maxCharacters = MediaContractLimits.MaxTimedTextCharacters)
    {
        if (Start < TimeSpan.Zero || End <= Start || string.IsNullOrWhiteSpace(Text))
        {
            return "invalid_timed_text";
        }

        if (Text.Length > maxCharacters || Text.Any(char.IsControl))
        {
            return "timed_text_limit_exceeded";
        }

        if (Confidence is double confidence &&
            (double.IsNaN(confidence) || double.IsInfinity(confidence) || confidence is < 0 or > 1))
        {
            return "invalid_confidence";
        }

        return null;
    }
}

public sealed record MediaJobRequest(
    string JobId,
    MediaJobKind Kind,
    IReadOnlyList<MediaAssetReference> Inputs,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    IReadOnlyDictionary<string, string>? Parameters = null)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(JobId))
        {
            return "job_id_required";
        }

        if (!MediaContractValidation.IsIdentifier(JobId, MediaContractLimits.MaxJobIdCharacters))
        {
            return "invalid_job_id";
        }

        if (!Enum.IsDefined(typeof(MediaJobKind), Kind))
        {
            return "unsupported_media_job";
        }

        if (Inputs is null || Inputs.Count == 0)
        {
            return "input_asset_required";
        }

        if (Inputs.Count > MediaContractLimits.MaxInputAssets)
        {
            return "input_asset_limit_exceeded";
        }

        foreach (var input in Inputs)
        {
            if (input is null)
            {
                return "invalid_input_asset";
            }

            var inputFailure = input.Validate();
            if (inputFailure is not null)
            {
                return inputFailure;
            }
        }

        if (CorrelationId is not null &&
            !MediaContractValidation.IsIdentifier(CorrelationId, MediaContractLimits.MaxCorrelationIdCharacters))
        {
            return "invalid_correlation_id";
        }

        if (IdempotencyKey is not null &&
            !MediaContractValidation.IsIdentifier(IdempotencyKey, MediaContractLimits.MaxIdempotencyKeyCharacters))
        {
            return "invalid_idempotency_key";
        }

        if (Parameters is null)
        {
            return null;
        }

        if (Parameters.Count > MediaContractLimits.MaxParameters)
        {
            return "parameters_limit_exceeded";
        }

        foreach (var parameter in Parameters)
        {
            if (!MediaContractValidation.IsIdentifier(parameter.Key, MediaContractLimits.MaxParameterKeyCharacters) ||
                string.IsNullOrWhiteSpace(parameter.Value) ||
                parameter.Value.Length > MediaContractLimits.MaxParameterValueCharacters ||
                parameter.Value.Any(char.IsControl))
            {
                return "invalid_parameter";
            }
        }

        return null;
    }

    public MediaJobResult Accepted() => MediaJobResult.Accepted(this);

    public MediaJobResult Running() => MediaJobResult.Running(this);
}

public sealed record MediaJobResult(
    string JobId,
    MediaJobStatus Status,
    string? FailureCode = null,
    string? FailureMessage = null,
    IReadOnlyList<MediaAssetReference>? Assets = null,
    IReadOnlyList<TimedTextSegment>? TimedText = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    MediaFailureCategory FailureCategory = MediaFailureCategory.None,
    IReadOnlyList<OcrFrameText>? OcrFrames = null)
{
    public bool IsTerminal => Status.IsTerminal();

    public MediaFailureCategory ErrorCategory =>
        FailureCategory == MediaFailureCategory.None
            ? MediaFailureCatalog.Classify(FailureCode)
            : FailureCategory;

    public string? SafeFailureMessage => MediaFailureCatalog.SanitizeMessage(FailureCode, FailureMessage);

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(JobId))
        {
            return "job_id_required";
        }

        if (!MediaContractValidation.IsIdentifier(JobId, MediaContractLimits.MaxJobIdCharacters))
        {
            return "invalid_job_id";
        }

        if (!Enum.IsDefined(typeof(MediaJobStatus), Status))
        {
            return "invalid_status";
        }

        if (CorrelationId is not null &&
            !MediaContractValidation.IsIdentifier(CorrelationId, MediaContractLimits.MaxCorrelationIdCharacters))
        {
            return "invalid_correlation_id";
        }

        if (IdempotencyKey is not null &&
            !MediaContractValidation.IsIdentifier(IdempotencyKey, MediaContractLimits.MaxIdempotencyKeyCharacters))
        {
            return "invalid_idempotency_key";
        }

        if (Status == MediaJobStatus.Failed && string.IsNullOrWhiteSpace(FailureCode))
        {
            return "failure_code_required";
        }

        if (FailureCode is not null &&
            (FailureCode.Length > MediaContractLimits.MaxFailureCodeCharacters ||
             FailureCode.Any(char.IsControl)))
        {
            return "invalid_failure_payload";
        }

        if (Status is not MediaJobStatus.Failed and not MediaJobStatus.Cancelled &&
            (!string.IsNullOrWhiteSpace(FailureCode) || !string.IsNullOrWhiteSpace(FailureMessage)))
        {
            return "invalid_failure_payload";
        }

        if (FailureCategory != MediaFailureCategory.None && string.IsNullOrWhiteSpace(FailureCode))
        {
            return "invalid_failure_payload";
        }

        if (Assets is not null)
        {
            foreach (var asset in Assets)
            {
                if (asset is null || asset.Validate() is not null)
                {
                    return "invalid_output_asset";
                }
            }
        }

        if (TimedText is not null)
        {
            foreach (var segment in TimedText)
            {
                if (segment is null || segment.Validate() is not null)
                {
                    return "invalid_timed_text";
                }
            }
        }

        if (OcrFrames is not null)
        {
            foreach (var frame in OcrFrames)
            {
                if (frame is null || !frame.IsValid || frame.Text.Length > MediaContractLimits.MaxTimedTextCharacters)
                {
                    return "invalid_ocr_result";
                }
            }
        }

        return null;
    }

    public static MediaJobResult Accepted(
        string jobId,
        string? correlationId = null,
        string? idempotencyKey = null) =>
        new(jobId, MediaJobStatus.Accepted, CorrelationId: correlationId, IdempotencyKey: idempotencyKey);

    public static MediaJobResult Accepted(MediaJobRequest request) =>
        Accepted(request.JobId, request.CorrelationId, request.IdempotencyKey);

    public static MediaJobResult Running(
        string jobId,
        string? correlationId = null,
        string? idempotencyKey = null) =>
        new(jobId, MediaJobStatus.Running, CorrelationId: correlationId, IdempotencyKey: idempotencyKey);

    public static MediaJobResult Running(MediaJobRequest request) =>
        Running(request.JobId, request.CorrelationId, request.IdempotencyKey);

    public static MediaJobResult Succeeded(
        string jobId,
        IReadOnlyList<MediaAssetReference>? assets = null,
        IReadOnlyList<TimedTextSegment>? timedText = null,
        string? correlationId = null,
        string? idempotencyKey = null) =>
        new(jobId, MediaJobStatus.Succeeded, Assets: assets, TimedText: timedText,
            CorrelationId: correlationId, IdempotencyKey: idempotencyKey);

    public static MediaJobResult Succeeded(MediaJobRequest request,
        IReadOnlyList<MediaAssetReference>? assets = null,
        IReadOnlyList<TimedTextSegment>? timedText = null) =>
        Succeeded(request.JobId, assets, timedText, request.CorrelationId, request.IdempotencyKey);

    public static MediaJobResult Failed(
        string jobId,
        string code,
        string message,
        string? correlationId = null,
        string? idempotencyKey = null)
    {
        var normalizedCode = code?.Trim();
        return new MediaJobResult(
            jobId,
            MediaJobStatus.Failed,
            normalizedCode,
            MediaFailureCatalog.SanitizeMessage(normalizedCode, message) ?? MediaFailureCatalog.SafeDefaultMessage(normalizedCode),
            CorrelationId: correlationId,
            IdempotencyKey: idempotencyKey,
            FailureCategory: MediaFailureCatalog.Classify(normalizedCode));
    }

    public static MediaJobResult Failed(MediaJobRequest request, string code, string message) =>
        Failed(request.JobId, code, message, request.CorrelationId, request.IdempotencyKey);

    public static MediaJobResult Cancelled(
        string jobId,
        string? correlationId = null,
        string? idempotencyKey = null) =>
        new(jobId, MediaJobStatus.Cancelled, "cancelled", "The media job was cancelled.",
            CorrelationId: correlationId,
            IdempotencyKey: idempotencyKey,
            FailureCategory: MediaFailureCategory.Cancelled);

    public static MediaJobResult Cancelled(MediaJobRequest request) =>
        Cancelled(request.JobId, request.CorrelationId, request.IdempotencyKey);

    public MediaJobResult WithContext(MediaJobRequest request) =>
        this with { CorrelationId = request.CorrelationId, IdempotencyKey = request.IdempotencyKey };

    public MediaJobResult WithContext(string? correlationId, string? idempotencyKey) =>
        this with { CorrelationId = correlationId, IdempotencyKey = idempotencyKey };
}

public static class MediaJobLifecycle
{
    public static bool IsTerminal(this MediaJobStatus status) =>
        status is MediaJobStatus.Succeeded or MediaJobStatus.Failed or MediaJobStatus.Cancelled;

    public static bool CanTransitionTo(this MediaJobStatus current, MediaJobStatus next)
    {
        if (!Enum.IsDefined(typeof(MediaJobStatus), current) ||
            !Enum.IsDefined(typeof(MediaJobStatus), next))
        {
            return false;
        }

        if (current == next)
        {
            return true;
        }

        return current switch
        {
            MediaJobStatus.Accepted => next is MediaJobStatus.Running or MediaJobStatus.Failed or MediaJobStatus.Cancelled,
            MediaJobStatus.Running => next is MediaJobStatus.Succeeded or MediaJobStatus.Failed or MediaJobStatus.Cancelled,
            _ => false
        };
    }

    public static bool TryTransition(
        MediaJobResult current,
        MediaJobStatus next,
        out MediaJobResult result)
    {
        if (!current.Status.CanTransitionTo(next))
        {
            result = MediaJobResult.Failed(
                current.JobId,
                "invalid_status_transition",
                "The media job cannot move to the requested state.",
                current.CorrelationId,
                current.IdempotencyKey);
            return false;
        }

        result = next switch
        {
            MediaJobStatus.Cancelled => MediaJobResult.Cancelled(
                current.JobId,
                current.CorrelationId,
                current.IdempotencyKey),
            MediaJobStatus.Failed => MediaJobResult.Failed(
                current.JobId,
                "job_failed",
                "The media job failed.",
                current.CorrelationId,
                current.IdempotencyKey),
            MediaJobStatus.Succeeded => current with
            {
                Status = MediaJobStatus.Succeeded,
                FailureCode = null,
                FailureMessage = null,
                FailureCategory = MediaFailureCategory.None
            },
            _ => current with { Status = next }
        };
        return true;
    }
}

public static class MediaContractValidation
{
    public static bool IsIdentifier(string? value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxCharacters)
        {
            return false;
        }

        return value.All(character =>
            character is >= 'a' and <= 'z' or
                >= 'A' and <= 'Z' or
                >= '0' and <= '9' or
                '-' or '_' or '.' or ':');
    }
}

public interface IChannelMediaCollector
{
    Task<MediaJobResult> CollectAsync(MediaJobRequest request, CancellationToken cancellationToken = default);
}

public interface ITranscriptionService
{
    Task<MediaJobResult> TranscribeAsync(MediaJobRequest request, CancellationToken cancellationToken = default);
}

public interface IFrameOcrService
{
    Task<MediaJobResult> RecognizeAsync(MediaJobRequest request, CancellationToken cancellationToken = default);
}

public interface ISpeechSynthesisService
{
    Task<MediaJobResult> SynthesizeAsync(MediaJobRequest request, CancellationToken cancellationToken = default);
}

public interface IVideoCompositionService
{
    Task<MediaJobResult> ComposeAsync(MediaJobRequest request, CancellationToken cancellationToken = default);
}
