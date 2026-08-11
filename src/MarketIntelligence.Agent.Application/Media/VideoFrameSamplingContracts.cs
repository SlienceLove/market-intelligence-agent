namespace MarketIntelligence.Agent.Application.Media;

public sealed class VideoFrameSamplingOptions
{
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int MaxFrames { get; init; } = 300;

    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record VideoFrameSample(
    TimeSpan Timestamp,
    string RelativePath,
    string MediaType = "image/jpeg");

public interface IVideoFrameSampler
{
    Task<IReadOnlyList<VideoFrameSample>> SampleAsync(
        MediaAssetReference video,
        string outputDirectory,
        VideoFrameSamplingOptions options,
        CancellationToken cancellationToken = default);
}

public static class FfmpegFrameSamplingArgumentBuilder
{
    public static ProcessRunRequest Build(
        MediaAssetReference video,
        string outputDirectory,
        VideoFrameSamplingOptions options)
    {
        if (!IsSafeAssetUri(video.Uri) ||
            !video.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Input must be a controlled video reference.", nameof(video));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory) ||
            Path.IsPathRooted(outputDirectory) ||
            outputDirectory.Contains(':', StringComparison.Ordinal) ||
            outputDirectory.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Output directory must be a controlled relative path.", nameof(outputDirectory));
        }

        if (options.SampleInterval <= TimeSpan.Zero ||
            options.MaxFrames <= 0 ||
            options.MaxDuration <= TimeSpan.Zero ||
            options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Sampling options are invalid.", nameof(options));
        }

        var fps = 1d / options.SampleInterval.TotalSeconds;
        var outputPattern = $"{outputDirectory.TrimEnd('/', '\\')}/frame-%06d.jpg";
        return new ProcessRunRequest(
            "ffmpeg",
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", video.Uri,
             "-t", options.MaxDuration.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
             "-vf", $"fps={fps.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}",
             "-frames:v", options.MaxFrames.ToString(System.Globalization.CultureInfo.InvariantCulture),
             "-q:v", "3", outputPattern],
            options.Timeout);
    }

    private static bool IsSafeAssetUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        (parsed.Scheme == "asset" || parsed.Scheme == "fixture");
}
