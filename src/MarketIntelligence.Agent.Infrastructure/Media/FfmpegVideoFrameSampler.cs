using System.Security.Cryptography;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Carries a stable media failure code so callers can map sampling problems onto the
/// shared vocabulary without inspecting exception text.
/// </summary>
public sealed class MediaSamplingException : Exception
{
    public MediaSamplingException(string failureCode, string message) : base(message)
    {
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

public sealed class FfmpegVideoFrameSampler : IVideoFrameSampler
{
    private readonly IMediaAssetPathResolver _resolver;
    private readonly IProcessRunner _processRunner;
    private readonly MediaOptions _options;

    public FfmpegVideoFrameSampler(
        IMediaAssetPathResolver resolver,
        IProcessRunner processRunner,
        IOptions<MediaOptions> options)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IReadOnlyList<VideoFrameSample>> SampleAsync(
        MediaAssetReference video,
        string outputDirectory,
        VideoFrameSamplingOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(options);

        cancellationToken.ThrowIfCancellationRequested();

        if (!_resolver.IsConfigured || string.IsNullOrWhiteSpace(_options.Ffmpeg.ExecutablePath))
        {
            throw new MediaSamplingException("provider_not_configured", "Frame sampling is not configured.");
        }

        // Reuse the contract-layer validation so the real path and the fake path
        // reject exactly the same inputs.
        FfmpegFrameSamplingArgumentBuilder.Build(video, outputDirectory, options);

        var input = _resolver.ResolveInput(video.Uri);
        if (!input.Succeeded)
        {
            throw new MediaSamplingException(input.FailureCode!, "Frame sampling input is not allowed.");
        }

        var output = _resolver.ResolveOutput(outputDirectory);
        if (!output.Succeeded)
        {
            throw new MediaSamplingException(output.FailureCode!, "Frame sampling output is not allowed.");
        }

        var frameDirectory = output.FullPath!;
        var maxFrames = Math.Min(
            options.MaxFrames,
            _options.MaxFrames > 0 ? _options.MaxFrames : options.MaxFrames);

        try
        {
            Directory.CreateDirectory(frameDirectory);

            var request = BuildRequest(input.FullPath!, frameDirectory, options, maxFrames);
            var result = await _processRunner.RunAsync(request, cancellationToken);

            if (result.Cancelled || cancellationToken.IsCancellationRequested)
            {
                CleanupDirectory(frameDirectory);
                throw new OperationCanceledException(cancellationToken);
            }

            if (result.TimedOut)
            {
                CleanupDirectory(frameDirectory);
                throw new MediaSamplingException("timeout", "Frame sampling timed out.");
            }

            if (result.ExitCode != 0)
            {
                CleanupDirectory(frameDirectory);
                throw new MediaSamplingException("frame_sampling_failed", "Frame sampling failed.");
            }

            var samples = CollectSamples(frameDirectory, options, maxFrames);
            if (samples.Count == 0)
            {
                CleanupDirectory(frameDirectory);
                throw new MediaSamplingException("empty_frame_sampling_result", "Frame sampling produced no frames.");
            }

            return samples;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CleanupDirectory(frameDirectory);
            throw new MediaSamplingException("frame_sampling_failed", "Frame sampling could not write frames.");
        }
    }

    private ProcessRunRequest BuildRequest(
        string inputPath,
        string frameDirectory,
        VideoFrameSamplingOptions options,
        int maxFrames)
    {
        var fps = 1d / options.SampleInterval.TotalSeconds;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var width = _options.Ffmpeg.MaxFrameWidth > 0 ? _options.Ffmpeg.MaxFrameWidth : 1920;
        var height = _options.Ffmpeg.MaxFrameHeight > 0 ? _options.Ffmpeg.MaxFrameHeight : 1080;

        // force_original_aspect_ratio=decrease with min() only ever downscales, so a
        // small source is never upscaled into a larger frame.
        var filter = string.Concat(
            "fps=", fps.ToString("0.######", culture),
            $",scale='min({width},iw)':'min({height},ih)':force_original_aspect_ratio=decrease");

        return new ProcessRunRequest(
            "ffmpeg",
            [
                "-nostdin", "-hide_banner", "-loglevel", "error",
                "-i", inputPath,
                "-t", options.MaxDuration.TotalSeconds.ToString("0.###", culture),
                "-vf", filter,
                "-frames:v", maxFrames.ToString(culture),
                "-q:v", "3",
                Path.Combine(frameDirectory, "frame-%06d.jpg")
            ],
            options.Timeout);
    }

    /// <summary>
    /// Maps produced files onto timestamps, drops consecutive duplicates, and enforces
    /// the frame ceiling by deleting anything FFmpeg produced beyond it.
    /// </summary>
    private static List<VideoFrameSample> CollectSamples(
        string frameDirectory,
        VideoFrameSamplingOptions options,
        int maxFrames)
    {
        var files = Directory
            .EnumerateFiles(frameDirectory, "frame-*.jpg", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var samples = new List<VideoFrameSample>(Math.Min(files.Count, maxFrames));
        string? previousPath = null;
        long previousLength = -1;
        string? previousHash = null;

        foreach (var file in files)
        {
            if (samples.Count >= maxFrames)
            {
                TryDelete(file);
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length == 0)
            {
                TryDelete(file);
                continue;
            }

            // Size first: cheap, and it filters most non-duplicates before hashing.
            if (info.Length == previousLength && previousPath is not null)
            {
                // The previous frame's hash is computed on demand and cached, so an
                // equal-length pair is compared against a real hash rather than null.
                previousHash ??= ComputeHash(previousPath);
                var hash = ComputeHash(file);

                if (hash is not null && previousHash is not null
                    && string.Equals(hash, previousHash, StringComparison.Ordinal))
                {
                    TryDelete(file);
                    continue;
                }

                previousHash = hash;
            }
            else
            {
                previousHash = null;
            }

            previousPath = file;
            previousLength = info.Length;

            // Timestamp from FFmpeg's own frame number, not the accepted count: dropping
            // a duplicate must not shift every later frame earlier than its source time.
            samples.Add(new VideoFrameSample(
                options.SampleInterval * FrameOrdinal(file, samples.Count),
                Path.GetFileName(file)));
        }

        return samples;
    }

    /// <summary>
    /// Reads the sequence number FFmpeg wrote into "frame-000001.jpg". Falls back to the
    /// positional index if the name does not carry one.
    /// </summary>
    private static int FrameOrdinal(string path, int fallback)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.LastIndexOf('-');
        if (separator < 0 || separator == name.Length - 1)
        {
            return fallback;
        }

        return int.TryParse(
            name.AsSpan(separator + 1),
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var ordinal) && ordinal > 0
            ? ordinal - 1
            : fallback;
    }

    private static string? ComputeHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leftover frames stay inside the controlled root; nothing escapes.
        }
    }

    private static void CleanupDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; the directory remains inside the controlled root.
        }
    }
}
