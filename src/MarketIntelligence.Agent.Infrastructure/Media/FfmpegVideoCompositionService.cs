using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Composes an authorized video with a synthesized audio track through a controlled
/// FFmpeg invocation. Every caller-influenced value passes the asset path resolver
/// before it becomes a process argument.
/// </summary>
public sealed class FfmpegVideoCompositionService : IVideoCompositionService
{
    private readonly IMediaAssetPathResolver _resolver;
    private readonly IProcessRunner _processRunner;
    private readonly IMediaProbe _probe;
    private readonly MediaOptions _options;

    public FfmpegVideoCompositionService(
        IMediaAssetPathResolver resolver,
        IProcessRunner processRunner,
        IMediaProbe probe,
        IOptions<MediaOptions> options)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<MediaJobResult> ComposeAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(request);
        }

        var requestFailure = request.Validate();
        if (requestFailure is not null)
        {
            return MediaJobResult.Failed(request, requestFailure, "Composition request is invalid.");
        }

        if (request.Kind != MediaJobKind.VideoComposition)
        {
            return MediaJobResult.Failed(request, "unsupported_media_job", "Composition service only accepts composition jobs.");
        }

        if (request.Inputs.Count < 2)
        {
            return MediaJobResult.Failed(request, "composition_inputs_required", "Composition requires video and audio assets.");
        }

        if (!_resolver.IsConfigured || string.IsNullOrWhiteSpace(_options.Ffmpeg.ExecutablePath))
        {
            return MediaJobResult.Failed(request, "provider_not_configured", "Video composition provider is not configured.");
        }

        var video = request.Inputs[0];
        var audio = request.Inputs[1];

        if (!video.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            !audio.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return MediaJobResult.Failed(request, "unsafe_composition_input", "Composition requires a video and an audio input.");
        }

        var videoPath = _resolver.ResolveInput(video.Uri);
        if (!videoPath.Succeeded)
        {
            return MediaJobResult.Failed(request, videoPath.FailureCode!, "Composition video input is not allowed.");
        }

        var audioPath = _resolver.ResolveInput(audio.Uri);
        if (!audioPath.Succeeded)
        {
            return MediaJobResult.Failed(request, audioPath.FailureCode!, "Composition audio input is not allowed.");
        }

        var output = _resolver.ResolveOutput($"media/{request.JobId}.mp4");
        if (!output.Succeeded)
        {
            return MediaJobResult.Failed(request, output.FailureCode!, "Composition output is not allowed.");
        }

        var outputPath = output.FullPath!;

        try
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var processRequest = new ProcessRunRequest(
                "ffmpeg",
                [
                    "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                    "-i", videoPath.FullPath!,
                    "-i", audioPath.FullPath!,
                    "-map", "0:v:0", "-map", "1:a:0",
                    "-shortest",
                    "-c:v", "libx264", "-c:a", "aac",
                    outputPath
                ],
                _options.Ffmpeg.Timeout > TimeSpan.Zero ? _options.Ffmpeg.Timeout : TimeSpan.FromMinutes(5));

            var result = await _processRunner.RunAsync(processRequest, cancellationToken);

            if (result.Cancelled || cancellationToken.IsCancellationRequested)
            {
                TryDelete(outputPath);
                return MediaJobResult.Cancelled(request);
            }

            if (result.TimedOut)
            {
                TryDelete(outputPath);
                return MediaJobResult.Failed(request, "composition_timeout", "Video composition timed out.");
            }

            if (result.ExitCode != 0)
            {
                TryDelete(outputPath);
                return MediaJobResult.Failed(request, "composition_failed", "Video composition failed.");
            }

            var info = new FileInfo(outputPath);
            if (!info.Exists || info.Length == 0)
            {
                TryDelete(outputPath);
                return MediaJobResult.Failed(request, "composition_output_missing", "Video composition produced no output.");
            }

            if (_options.MaxOutputBytes > 0 && info.Length > _options.MaxOutputBytes)
            {
                TryDelete(outputPath);
                return MediaJobResult.Failed(request, "composition_output_too_large", "Video composition output exceeded the configured size limit.");
            }

            var durations = await _probe.ProbeAsync(outputPath, cancellationToken);
            var maxDrift = _options.Ffmpeg.MaxAudioVideoDrift;
            if (durations?.Drift is { } drift && maxDrift > TimeSpan.Zero && drift > maxDrift)
            {
                TryDelete(outputPath);
                return MediaJobResult.Failed(request, "composition_av_drift", "Composed audio and video durations diverged beyond the allowed drift.");
            }

            return new MediaJobResult(
                request.JobId,
                MediaJobStatus.Succeeded,
                CorrelationId: request.CorrelationId,
                IdempotencyKey: request.IdempotencyKey,
                Assets:
                [
                    new MediaAssetReference(
                        $"asset://media/{request.JobId}.mp4",
                        "video/mp4",
                        info.Length,
                        durations?.Container ?? video.Duration)
                ]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(outputPath);
            return MediaJobResult.Failed(request, "composition_failed", "Video composition could not write its output.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The partial output stays inside the controlled root.
        }
    }
}
