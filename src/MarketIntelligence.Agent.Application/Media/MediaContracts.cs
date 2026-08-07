namespace MarketIntelligence.Agent.Application.Media;

public enum MediaJobKind
{
    Collection,
    Transcription,
    FrameOcr,
    SpeechSynthesis,
    VideoComposition
}

public enum MediaJobStatus
{
    Succeeded,
    Failed,
    Cancelled
}

public sealed record MediaAssetReference(
    string Uri,
    string MediaType,
    long? SizeBytes = null,
    TimeSpan? Duration = null);

public sealed record TimedTextSegment(
    TimeSpan Start,
    TimeSpan End,
    string Text,
    double? Confidence = null)
{
    public bool IsValid => Start >= TimeSpan.Zero && End > Start && !string.IsNullOrWhiteSpace(Text);
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

        if (Inputs is null || Inputs.Count == 0)
        {
            return "input_asset_required";
        }

        if (Inputs.Any(input => string.IsNullOrWhiteSpace(input.Uri) ||
                                string.IsNullOrWhiteSpace(input.MediaType)))
        {
            return "invalid_input_asset";
        }

        return null;
    }
}

public sealed record MediaJobResult(
    string JobId,
    MediaJobStatus Status,
    string? FailureCode = null,
    string? FailureMessage = null,
    IReadOnlyList<MediaAssetReference>? Assets = null,
    IReadOnlyList<TimedTextSegment>? TimedText = null)
{
    public static MediaJobResult Failed(string jobId, string code, string message) =>
        new(jobId, MediaJobStatus.Failed, code, message);

    public static MediaJobResult Cancelled(string jobId) =>
        new(jobId, MediaJobStatus.Cancelled, "cancelled", "The media job was cancelled.");
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
