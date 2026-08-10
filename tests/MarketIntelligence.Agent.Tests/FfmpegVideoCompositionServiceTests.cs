using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>F-T12, plus the composition guard rails around it.</summary>
public sealed class FfmpegVideoCompositionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _ffmpegStub;

    public FfmpegVideoCompositionServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mi-composition-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "video"));
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        File.WriteAllText(Path.Combine(_root, "video", "clip.mp4"), "video-source");
        File.WriteAllText(Path.Combine(_root, "audio", "voice.wav"), "audio-source");

        _ffmpegStub = Path.Combine(_root, "ffmpeg.exe");
        File.WriteAllText(_ffmpegStub, "stub");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }

    /// <summary>Writes a stand-in output file so the post-run checks have something real.</summary>
    private sealed class OutputWritingRunner : IProcessRunner
    {
        private readonly string _content;
        private readonly ProcessRunResult _result;

        public OutputWritingRunner(string content = "composed", ProcessRunResult? result = null)
        {
            _content = content;
            _result = result ?? new ProcessRunResult(0, string.Empty, TimeSpan.FromMilliseconds(5));
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default)
        {
            var output = request.Arguments[^1];
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, _content);
            return Task.FromResult(_result);
        }
    }

    private sealed class StubProbe : IMediaProbe
    {
        private readonly MediaDurations? _durations;

        public StubProbe(MediaDurations? durations) => _durations = durations;

        public bool IsConfigured => true;

        public Task<MediaDurations?> ProbeAsync(string fullPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_durations);
    }

    private FfmpegVideoCompositionService CreateService(
        IProcessRunner runner,
        IMediaProbe probe,
        long maxOutputBytes = 0,
        TimeSpan? maxDrift = null)
    {
        var options = new MediaOptions { AssetRoot = _root, MaxOutputBytes = maxOutputBytes };
        options.Ffmpeg.ExecutablePath = _ffmpegStub;
        options.Ffmpeg.ProbeExecutablePath = _ffmpegStub;
        options.Ffmpeg.MaxAudioVideoDrift = maxDrift ?? TimeSpan.FromMilliseconds(200);

        var resolver = new MediaAssetPathResolver(Options.Create(options));
        return new FfmpegVideoCompositionService(resolver, runner, probe, Options.Create(options));
    }

    private static MediaJobRequest Request(string jobId = "job-compose-1") => new(
        jobId,
        MediaJobKind.VideoComposition,
        [
            new MediaAssetReference("asset://video/clip.mp4", "video/mp4"),
            new MediaAssetReference("asset://audio/voice.wav", "audio/wav")
        ]);

    [Fact]
    public async Task Returns_an_asset_uri_and_never_a_filesystem_path()
    {
        var service = CreateService(
            new OutputWritingRunner(),
            new StubProbe(new MediaDurations(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10))));

        var result = await service.ComposeAsync(Request());

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        var output = Assert.Single(result.Assets!);
        Assert.Equal("asset://media/job-compose-1.mp4", output.Uri);
        Assert.DoesNotContain(_root, output.Uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fails_when_audio_and_video_drift_beyond_the_limit()
    {
        var service = CreateService(
            new OutputWritingRunner(),
            new StubProbe(new MediaDurations(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(9.5))),
            maxDrift: TimeSpan.FromMilliseconds(200));

        var result = await service.ComposeAsync(Request("job-drift"));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("composition_av_drift", result.FailureCode);

        // A rejected composition must not leave its output behind.
        Assert.False(File.Exists(Path.Combine(_root, "media", "job-drift.mp4")));
    }

    [Fact]
    public async Task Accepts_drift_within_the_limit()
    {
        var service = CreateService(
            new OutputWritingRunner(),
            new StubProbe(new MediaDurations(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(9.95))),
            maxDrift: TimeSpan.FromMilliseconds(200));

        var result = await service.ComposeAsync(Request("job-ok-drift"));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Succeeds_when_the_probe_cannot_report_durations()
    {
        // An unavailable probe must not fail an otherwise good composition.
        var service = CreateService(new OutputWritingRunner(), new StubProbe(null));

        var result = await service.ComposeAsync(Request("job-no-probe"));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Ignores_drift_when_only_one_stream_is_present()
    {
        var service = CreateService(
            new OutputWritingRunner(),
            new StubProbe(new MediaDurations(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), null)));

        var result = await service.ComposeAsync(Request("job-single-stream"));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
    }

    [Fact]
    public async Task Fails_when_ffmpeg_produces_no_output()
    {
        var service = CreateService(
            new EmptyRunner(new ProcessRunResult(0, string.Empty, TimeSpan.FromMilliseconds(5))),
            new StubProbe(null));

        var result = await service.ComposeAsync(Request("job-missing"));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("composition_output_missing", result.FailureCode);
    }

    [Fact]
    public async Task Fails_when_the_output_is_zero_length()
    {
        var service = CreateService(new OutputWritingRunner(string.Empty), new StubProbe(null));

        var result = await service.ComposeAsync(Request("job-empty"));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("composition_output_missing", result.FailureCode);
    }

    [Fact]
    public async Task Fails_and_removes_an_oversized_output()
    {
        var service = CreateService(
            new OutputWritingRunner(new string('x', 512)),
            new StubProbe(null),
            maxOutputBytes: 64);

        var result = await service.ComposeAsync(Request("job-too-large"));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("composition_output_too_large", result.FailureCode);
        Assert.False(File.Exists(Path.Combine(_root, "media", "job-too-large.mp4")));
    }

    [Fact]
    public async Task Fails_when_ffmpeg_exits_nonzero()
    {
        var service = CreateService(
            new OutputWritingRunner("partial", new ProcessRunResult(1, "encoder error", TimeSpan.FromMilliseconds(5))),
            new StubProbe(null));

        var result = await service.ComposeAsync(Request("job-failed"));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("composition_failed", result.FailureCode);
        Assert.False(File.Exists(Path.Combine(_root, "media", "job-failed.mp4")));
    }

    [Fact]
    public async Task Reports_timeout_distinctly()
    {
        var service = CreateService(
            new OutputWritingRunner("partial", new ProcessRunResult(-1, "slow", TimeSpan.FromMinutes(6), TimedOut: true)),
            new StubProbe(null));

        var result = await service.ComposeAsync(Request("job-timeout"));

        Assert.Equal("composition_timeout", result.FailureCode);
        Assert.False(File.Exists(Path.Combine(_root, "media", "job-timeout.mp4")));
    }

    [Fact]
    public async Task Rejects_swapped_media_types()
    {
        var service = CreateService(new OutputWritingRunner(), new StubProbe(null));
        var request = new MediaJobRequest(
            "job-swapped",
            MediaJobKind.VideoComposition,
            [
                new MediaAssetReference("asset://audio/voice.wav", "audio/wav"),
                new MediaAssetReference("asset://video/clip.mp4", "video/mp4")
            ]);

        var result = await service.ComposeAsync(request);

        Assert.Equal("unsafe_composition_input", result.FailureCode);
    }

    [Fact]
    public async Task Rejects_an_input_that_escapes_the_asset_root()
    {
        var service = CreateService(new OutputWritingRunner(), new StubProbe(null));
        var request = new MediaJobRequest(
            "job-escape",
            MediaJobKind.VideoComposition,
            [
                new MediaAssetReference("asset://video/../../outside.mp4", "video/mp4"),
                new MediaAssetReference("asset://audio/voice.wav", "audio/wav")
            ]);

        var result = await service.ComposeAsync(request);

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    [Fact]
    public async Task Reports_provider_not_configured_without_an_asset_root()
    {
        var options = new MediaOptions { AssetRoot = string.Empty };
        options.Ffmpeg.ExecutablePath = _ffmpegStub;
        var resolver = new MediaAssetPathResolver(Options.Create(options));
        var service = new FfmpegVideoCompositionService(
            resolver,
            new OutputWritingRunner(),
            new StubProbe(null),
            Options.Create(options));

        var result = await service.ComposeAsync(Request("job-unconfigured"));

        Assert.Equal("provider_not_configured", result.FailureCode);
    }

    private sealed class EmptyRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;

        public EmptyRunner(ProcessRunResult result) => _result = result;

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}
