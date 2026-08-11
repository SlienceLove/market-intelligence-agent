namespace MarketIntelligence.Agent.Application.Media;

public sealed record OcrBoundingBox(
    double X,
    double Y,
    double Width,
    double Height)
{
    public bool IsValid => X >= 0 && Y >= 0 && Width > 0 && Height > 0;
}

public sealed record OcrFrameText(
    TimeSpan Timestamp,
    string Text,
    OcrBoundingBox? Bounds = null,
    string? Language = null,
    double? Confidence = null)
{
    public bool IsValid => Timestamp >= TimeSpan.Zero &&
                           !string.IsNullOrWhiteSpace(Text) &&
                           (Bounds is null || Bounds.IsValid);
}

public sealed class FrameOcrOptions
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxFrames { get; init; } = 300;

    public int MaxTextCharacters { get; init; } = 500;

    public int MaxTotalCharacters { get; init; } = 10_000;

    public double DuplicateSimilarityThreshold { get; init; } = 0.95;
}

public static class FrameOcrInputPolicy
{
    public static string? Validate(MediaAssetReference asset, FrameOcrOptions options)
    {
        if (!asset.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !asset.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "unsupported_ocr_format";
        }

        if (asset.SizeBytes is <= 0 || asset.SizeBytes > 50 * 1024 * 1024)
        {
            return "ocr_input_size_exceeded";
        }

        if (asset.Duration is { } duration && duration <= TimeSpan.Zero)
        {
            return "ocr_duration_invalid";
        }

        if (options.MaxFrames <= 0 || options.SampleInterval <= TimeSpan.Zero)
        {
            return "ocr_sampling_invalid";
        }

        return null;
    }
}

public static class OcrResultNormalizer
{
    public static IReadOnlyList<OcrFrameText> Normalize(
        IEnumerable<OcrFrameText> source,
        FrameOcrOptions options)
    {
        var normalized = new List<OcrFrameText>();
        var totalCharacters = 0;

        foreach (var frame in source.OrderBy(item => item.Timestamp))
        {
            if (!frame.IsValid || normalized.Count >= options.MaxFrames)
            {
                continue;
            }

            var remaining = options.MaxTotalCharacters - totalCharacters;
            if (remaining <= 0)
            {
                break;
            }

            var text = frame.Text.Trim();
            var maxCharacters = Math.Min(options.MaxTextCharacters, remaining);
            if (text.Length > maxCharacters)
            {
                text = text[..maxCharacters];
            }

            var confidence = frame.Confidence is null
                ? (double?)null
                : Math.Clamp(frame.Confidence.Value, 0, 1);
            var candidate = frame with { Text = text, Confidence = confidence };

            var previous = normalized.LastOrDefault();
            if (previous is not null &&
                string.Equals(previous.Text, candidate.Text, StringComparison.Ordinal) &&
                candidate.Timestamp - previous.Timestamp <= options.SampleInterval)
            {
                continue;
            }

            normalized.Add(candidate);
            totalCharacters += text.Length;
        }

        return normalized;
    }
}
