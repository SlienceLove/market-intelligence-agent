namespace MarketIntelligence.Agent.Application.Media;

public sealed record ProcessRunRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

public sealed record ProcessRunResult(
    int ExitCode,
    string ErrorSummary,
    TimeSpan Duration,
    bool TimedOut = false,
    bool Cancelled = false,
    string StandardOutput = "");

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default);
}

public static class FfmpegArgumentBuilder
{
    public static ProcessRunRequest Build(
        MediaAssetReference video,
        MediaAssetReference audio,
        string outputPath,
        TimeSpan timeout)
    {
        if (!IsSafeAssetUri(video.Uri) || !IsSafeAssetUri(audio.Uri))
        {
            throw new ArgumentException("Input assets must use controlled references.");
        }

        if (string.IsNullOrWhiteSpace(outputPath) ||
            Path.IsPathRooted(outputPath) ||
            outputPath.Contains(':' , StringComparison.Ordinal) ||
            outputPath.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("Output path must be a controlled relative path.", nameof(outputPath));
        }

        return new ProcessRunRequest(
            "ffmpeg",
            ["-y", "-i", video.Uri, "-i", audio.Uri, "-shortest", "-c:v", "libx264", "-c:a", "aac", outputPath],
            timeout);
    }

    private static bool IsSafeAssetUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
        (parsed.Scheme == "asset" || parsed.Scheme == "fixture");
}

public sealed class FakeProcessRunner : IProcessRunner
{
    public Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(new ProcessRunResult(-1, "cancelled", TimeSpan.Zero, Cancelled: true));
        }

        return Task.FromResult(new ProcessRunResult(0, string.Empty, TimeSpan.FromSeconds(1)));
    }
}

public sealed class FakeVideoCompositionService(
    IProcessRunner processRunner) : IVideoCompositionService
{
    public async Task<MediaJobResult> ComposeAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(request.JobId);
        }

        var requestFailure = request.Validate();
        if (requestFailure is not null)
        {
            return MediaJobResult.Failed(request.JobId, requestFailure, "Composition request is invalid.");
        }

        if (request.Kind != MediaJobKind.VideoComposition || request.Inputs.Count < 2)
        {
            return MediaJobResult.Failed(request.JobId, "composition_inputs_required", "Composition requires video and audio assets.");
        }

        ProcessRunRequest processRequest;
        try
        {
            processRequest = FfmpegArgumentBuilder.Build(
                request.Inputs[0],
                request.Inputs[1],
                $"media/{request.JobId}.mp4",
                TimeSpan.FromMinutes(5));
        }
        catch (ArgumentException exception)
        {
            return MediaJobResult.Failed(request.JobId, "unsafe_composition_input", exception.Message);
        }

        var processResult = await processRunner.RunAsync(processRequest, cancellationToken);
        if (processResult.Cancelled || cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(request.JobId);
        }

        if (processResult.TimedOut)
        {
            return MediaJobResult.Failed(request.JobId, "composition_timeout", "Video composition timed out.");
        }

        if (processResult.ExitCode != 0)
        {
            return MediaJobResult.Failed(request.JobId, "composition_failed", "Video composition failed.");
        }

        return new MediaJobResult(
            request.JobId,
            MediaJobStatus.Succeeded,
            Assets: [new MediaAssetReference(
                $"asset://fixture/video/{request.JobId}",
                "video/mp4",
                4096,
                request.Inputs[0].Duration)]);
    }
}
