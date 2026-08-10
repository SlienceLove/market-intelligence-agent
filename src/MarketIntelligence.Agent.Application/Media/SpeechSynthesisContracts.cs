namespace MarketIntelligence.Agent.Application.Media;

public sealed class SpeechSynthesisOptions
{
    public int MaxTextCharacters { get; init; } = 10_000;

    public int MaxChunkCharacters { get; init; } = 800;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(10);

    public IReadOnlySet<string> AllowedVoices { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default" };

    public IReadOnlySet<string> AllowedLanguages { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "zh-CN", "en-US" };
}

public static class SpeechSynthesisInputPolicy
{
    public static string? Validate(
        MediaJobRequest request,
        SpeechSynthesisOptions options,
        out string text,
        out string voice,
        out string language)
    {
        text = request.Parameters?.GetValueOrDefault("text")?.Trim() ?? string.Empty;
        voice = request.Parameters?.GetValueOrDefault("voice")?.Trim() ?? "default";
        language = request.Parameters?.GetValueOrDefault("language")?.Trim() ?? "zh-CN";

        if (request.Kind != MediaJobKind.SpeechSynthesis)
        {
            return "unsupported_media_job";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "speech_text_required";
        }

        if (text.Length > options.MaxTextCharacters)
        {
            return "speech_text_too_long";
        }

        if (!options.AllowedVoices.Contains(voice))
        {
            return "voice_not_allowed";
        }

        if (!options.AllowedLanguages.Contains(language))
        {
            return "language_not_allowed";
        }

        return null;
    }
}

public static class SpeechTextChunker
{
    public static IReadOnlyList<string> Split(string text, int maxCharacters)
    {
        if (maxCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        var chunks = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > maxCharacters)
        {
            var splitAt = FindSplitPoint(remaining, maxCharacters);
            chunks.Add(remaining[..splitAt].Trim());
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0)
        {
            chunks.Add(remaining);
        }

        return chunks;
    }

    private static int FindSplitPoint(string text, int maxCharacters)
    {
        var candidate = text.LastIndexOfAny(['。', '！', '？', '.', '!', '?', ',', '，'], maxCharacters - 1);
        return candidate > 0 ? candidate + 1 : maxCharacters;
    }
}

public sealed class FakeSpeechSynthesisService(
    SpeechSynthesisOptions? options = null) : ISpeechSynthesisService
{
    private readonly SpeechSynthesisOptions _options = options ?? new();

    public Task<MediaJobResult> SynthesizeAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(MediaJobResult.Cancelled(request.JobId));
        }

        var requestFailure = request.Validate();
        if (requestFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request.JobId, requestFailure, "Speech request is invalid."));
        }

        var inputFailure = SpeechSynthesisInputPolicy.Validate(
            request,
            _options,
            out var text,
            out _,
            out _);
        if (inputFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request.JobId, inputFailure, "Speech input is not allowed."));
        }

        var chunks = SpeechTextChunker.Split(text, _options.MaxChunkCharacters);
        var duration = TimeSpan.FromSeconds(Math.Max(1, text.Length / 5.0));
        if (duration > _options.MaxDuration)
        {
            return Task.FromResult(MediaJobResult.Failed(request.JobId, "speech_duration_exceeded", "Speech duration exceeds the configured limit."));
        }

        return Task.FromResult(new MediaJobResult(
            request.JobId,
            MediaJobStatus.Succeeded,
            Assets: [new MediaAssetReference(
                $"asset://fixture/audio/{request.JobId}",
                "audio/wav",
                Math.Max(1024, chunks.Count * 1024),
                duration)]));
    }
}
