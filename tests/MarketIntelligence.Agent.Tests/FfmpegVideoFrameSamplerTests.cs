using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>F-T09 through F-T11.</summary>
public sealed class FfmpegVideoFrameSamplerTests : IDisposable
{
    private readonly string _root;
    private readonly string _ffmpegStub;

    public FfmpegVideoFrameSamplerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mi-sampler-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "video"));
        File.WriteAllText(Path.Combine(_root, "video", "demo.mp4"), "source");

        // The runner is stubbed, so this only has to exist to satisfy the configuration
        // check; it is never launched.
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

    /// <summary>Stands in for FFmpeg by writing the frames a real run would produce.</summary>
    private sealed class FrameWritingRunner : IProcessRunner
    {
        private readonly Action<string> _writeFrames;
        private readonly ProcessRunResult _result;

        public FrameWritingRunner(Action<string> writeFrames, ProcessRunResult? result = null)
        {
            _writeFrames = writeFrames;
            _result = result ?? new ProcessRunResult(0, string.Empty, TimeSpan.FromMilliseconds(5));
        }

        public IReadOnlyList<string> LastArguments { get; private set; } = [];

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default)
        {
            LastArguments = request.Arguments;

            // The output pattern is the last argument; frames land beside it.
            var directory = Path.GetDirectoryName(request.Arguments[^1])!;
            Directory.CreateDirectory(directory);
            _writeFrames(directory);

            return Task.FromResult(_result);
        }
    }

    private FfmpegVideoFrameSampler CreateSampler(IProcessRunner runner, int maxFrames = 0)
    {
        var options = new MediaOptions { AssetRoot = _root, MaxFrames = maxFrames };
        options.Ffmpeg.ExecutablePath = _ffmpegStub;
        var resolver = new MediaAssetPathResolver(Options.Create(options));
        return new FfmpegVideoFrameSampler(resolver, runner, Options.Create(options));
    }

    private static void WriteFrames(string directory, params string[] contents)
    {
        for (var i = 0; i < contents.Length; i++)
        {
            File.WriteAllText(
                Path.Combine(directory, $"frame-{i + 1:D6}.jpg"),
                contents[i]);
        }
    }

    private static MediaAssetReference Video() => new("asset://video/demo.mp4", "video/mp4");

    [Fact]
    public async Task Maps_frames_onto_the_sample_interval()
    {
        var runner = new FrameWritingRunner(d => WriteFrames(d, "a", "bb", "ccc"));
        var sampler = CreateSampler(runner);
        var options = new VideoFrameSamplingOptions { SampleInterval = TimeSpan.FromSeconds(2) };

        var samples = await sampler.SampleAsync(Video(), "frames/job-1", options);

        Assert.Equal(3, samples.Count);
        Assert.Equal(TimeSpan.Zero, samples[0].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(2), samples[1].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(4), samples[2].Timestamp);

        // Relative names only: a filesystem path must never reach a caller.
        Assert.All(samples, sample => Assert.False(Path.IsPathRooted(sample.RelativePath)));
        Assert.Equal("frame-000001.jpg", samples[0].RelativePath);
    }

    [Fact]
    public async Task Passes_the_frame_ceiling_to_ffmpeg_and_enforces_it_on_the_result()
    {
        // FFmpeg is told "-frames:v 2" but overproduces; the ceiling still holds.
        var runner = new FrameWritingRunner(d => WriteFrames(d, "a", "bb", "ccc", "dddd"));
        var sampler = CreateSampler(runner, maxFrames: 2);
        var options = new VideoFrameSamplingOptions { MaxFrames = 10 };

        var samples = await sampler.SampleAsync(Video(), "frames/job-2", options);

        Assert.Equal(2, samples.Count);

        var arguments = runner.LastArguments;
        var framesFlag = arguments.ToList().IndexOf("-frames:v");
        Assert.True(framesFlag >= 0);
        Assert.Equal("2", arguments[framesFlag + 1]);

        // Frames beyond the ceiling are removed, not left behind.
        var remaining = Directory.GetFiles(Path.Combine(_root, "frames", "job-2"), "frame-*.jpg");
        Assert.Equal(2, remaining.Length);
    }

    [Fact]
    public async Task Drops_the_first_consecutive_duplicate_pair()
    {
        // Frames 2 and 3 are byte-identical. Both the pair and any later repeat must go.
        var runner = new FrameWritingRunner(d => WriteFrames(d, "a", "same", "same", "zzzz"));
        var sampler = CreateSampler(runner);
        var options = new VideoFrameSamplingOptions { SampleInterval = TimeSpan.FromSeconds(1) };

        var samples = await sampler.SampleAsync(Video(), "frames/job-3", options);

        Assert.Equal(3, samples.Count);
        Assert.Equal(
            ["frame-000001.jpg", "frame-000002.jpg", "frame-000004.jpg"],
            samples.Select(sample => sample.RelativePath));

        // Timestamps track the source frame number, so dropping frame 3 leaves frame 4
        // at 3s rather than pulling it back to 2s.
        Assert.Equal(TimeSpan.FromSeconds(3), samples[2].Timestamp);
    }

    [Fact]
    public async Task Keeps_equal_length_frames_that_differ_in_content()
    {
        var runner = new FrameWritingRunner(d => WriteFrames(d, "aaaa", "bbbb", "cccc"));
        var sampler = CreateSampler(runner);

        var samples = await sampler.SampleAsync(Video(), "frames/job-4", new VideoFrameSamplingOptions());

        Assert.Equal(3, samples.Count);
    }

    [Fact]
    public async Task Skips_zero_length_frames()
    {
        var runner = new FrameWritingRunner(d => WriteFrames(d, "a", string.Empty, "ccc"));
        var sampler = CreateSampler(runner);

        var samples = await sampler.SampleAsync(Video(), "frames/job-5", new VideoFrameSamplingOptions());

        Assert.Equal(2, samples.Count);
        Assert.DoesNotContain("frame-000002.jpg", samples.Select(sample => sample.RelativePath));
    }

    [Fact]
    public async Task Cleans_up_and_reports_when_ffmpeg_fails()
    {
        var runner = new FrameWritingRunner(
            d => WriteFrames(d, "partial"),
            new ProcessRunResult(1, "decode error", TimeSpan.FromMilliseconds(5)));
        var sampler = CreateSampler(runner);

        var exception = await Assert.ThrowsAsync<MediaSamplingException>(
            () => sampler.SampleAsync(Video(), "frames/job-6", new VideoFrameSamplingOptions()));

        Assert.Equal("frame_sampling_failed", exception.FailureCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "frames", "job-6")));
    }

    [Fact]
    public async Task Cleans_up_and_reports_on_timeout()
    {
        var runner = new FrameWritingRunner(
            d => WriteFrames(d, "partial"),
            new ProcessRunResult(-1, "slow", TimeSpan.FromMinutes(6), TimedOut: true));
        var sampler = CreateSampler(runner);

        var exception = await Assert.ThrowsAsync<MediaSamplingException>(
            () => sampler.SampleAsync(Video(), "frames/job-7", new VideoFrameSamplingOptions()));

        Assert.Equal("timeout", exception.FailureCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "frames", "job-7")));
    }

    [Fact]
    public async Task Cleans_up_and_reports_when_no_frames_are_produced()
    {
        var runner = new FrameWritingRunner(_ => { });
        var sampler = CreateSampler(runner);

        var exception = await Assert.ThrowsAsync<MediaSamplingException>(
            () => sampler.SampleAsync(Video(), "frames/job-8", new VideoFrameSamplingOptions()));

        Assert.Equal("empty_frame_sampling_result", exception.FailureCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "frames", "job-8")));
    }

    [Fact]
    public async Task Rejects_an_input_outside_the_asset_root()
    {
        var runner = new FrameWritingRunner(d => WriteFrames(d, "a"));
        var sampler = CreateSampler(runner);
        var outside = new MediaAssetReference("asset://video/../../escape.mp4", "video/mp4");

        var exception = await Assert.ThrowsAsync<MediaSamplingException>(
            () => sampler.SampleAsync(outside, "frames/job-9", new VideoFrameSamplingOptions()));

        Assert.Equal("unsafe_asset_reference", exception.FailureCode);
    }

    [Fact]
    public async Task Reports_provider_not_configured_when_ffmpeg_is_missing()
    {
        var options = new MediaOptions { AssetRoot = _root };
        options.Ffmpeg.ExecutablePath = string.Empty;
        var resolver = new MediaAssetPathResolver(Options.Create(options));
        var sampler = new FfmpegVideoFrameSampler(
            resolver,
            new FrameWritingRunner(d => WriteFrames(d, "a")),
            Options.Create(options));

        var exception = await Assert.ThrowsAsync<MediaSamplingException>(
            () => sampler.SampleAsync(Video(), "frames/job-10", new VideoFrameSamplingOptions()));

        Assert.Equal("provider_not_configured", exception.FailureCode);
    }

    [Fact]
    public async Task Only_ever_downscales_frames()
    {
        var runner = new FrameWritingRunner(d => WriteFrames(d, "a"));
        var sampler = CreateSampler(runner);

        await sampler.SampleAsync(Video(), "frames/job-11", new VideoFrameSamplingOptions());

        var filterIndex = runner.LastArguments.ToList().IndexOf("-vf");
        var filter = runner.LastArguments[filterIndex + 1];

        Assert.Contains("min(1920,iw)", filter, StringComparison.Ordinal);
        Assert.Contains("force_original_aspect_ratio=decrease", filter, StringComparison.Ordinal);
    }
}
