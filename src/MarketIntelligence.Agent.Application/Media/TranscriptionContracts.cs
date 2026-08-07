namespace MarketIntelligence.Agent.Application.Media;

public sealed class TranscriptionOptions
{
    public long MaxInputBytes { get; init; } = 50 * 1024 * 1024;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    public int MaxSegmentCharacters { get; init; } = 500;

    public int MaxTotalCharacters { get; init; } = 10_000;
}

public static class TranscriptionInputPolicy
{
    public static string? Validate(MediaAssetReference asset, TranscriptionOptions options)
    {
        if (!asset.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "unsupported_audio_format";
        }

        if (asset.SizeBytes is <= 0 || asset.SizeBytes > options.MaxInputBytes)
        {
            return "audio_size_exceeded";
        }

        if (asset.Duration is null || asset.Duration <= TimeSpan.Zero || asset.Duration > options.MaxDuration)
        {
            return "audio_duration_exceeded";
        }

        return null;
    }
}

public static class TimedTextNormalizer
{
    public static MediaJobResult Normalize(
        string jobId,
        IEnumerable<TimedTextSegment> source,
        TranscriptionOptions options)
    {
        var normalized = new List<TimedTextSegment>();
        var totalCharacters = 0;
        var previousEnd = TimeSpan.Zero;

        foreach (var sourceSegment in source.OrderBy(segment => segment.Start))
        {
            var start = sourceSegment.Start < previousEnd ? previousEnd : sourceSegment.Start;
            var end = sourceSegment.End;
            if (end <= start || string.IsNullOrWhiteSpace(sourceSegment.Text))
            {
                continue;
            }

            var remaining = options.MaxTotalCharacters - totalCharacters;
            if (remaining <= 0)
            {
                break;
            }

            var text = sourceSegment.Text.Trim();
            var maxCharacters = Math.Min(options.MaxSegmentCharacters, remaining);
            if (text.Length > maxCharacters)
            {
                text = text[..maxCharacters];
            }

            normalized.Add(new TimedTextSegment(
                start,
                end,
                text,
                sourceSegment.Confidence is null
                    ? null
                    : Math.Clamp(sourceSegment.Confidence.Value, 0, 1)));
            totalCharacters += text.Length;
            previousEnd = end;
        }

        return normalized.Count == 0
            ? MediaJobResult.Failed(jobId, "empty_transcript", "Transcription returned no usable segments.")
            : new MediaJobResult(jobId, MediaJobStatus.Succeeded, TimedText: normalized);
    }
}
