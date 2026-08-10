using System.Diagnostics;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Drives the real FFmpeg binaries through the real .NET service layer. Every other
/// FFmpeg test stubs <see cref="IProcessRunner"/>, which leaves the argument vector
/// itself unverified — these confirm FFmpeg actually accepts what we build.
///
/// Opt-in via environment variables so the suite still runs where FFmpeg is absent:
///   MI_SMOKE_FFMPEG=&lt;path to ffmpeg&gt;  MI_SMOKE_FFPROBE=&lt;path to ffprobe&gt;
/// </summary>
public sealed class FfmpegRealBinarySmokeTests : IDisposable
{
    private readonly string? _ffmpeg = Environment.GetEnvironmentVariable("MI_SMOKE_FFMPEG");
    private readonly string? _ffprobe = Environment.GetEnvironmentVariable("MI_SMOKE_FFPROBE");
    private readonly string _root;

    public FfmpegRealBinarySmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mi-ffmpeg-smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "video"));
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
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

    private bool Enabled =>
        !string.IsNullOrWhiteSpace(_ffmpeg) && File.Exists(_ffmpeg) &&
        !string.IsNullOrWhiteSpace(_ffprobe) && File.Exists(_ffprobe);

    private MediaOptions CreateOptions()
    {
        var options = new MediaOptions { AssetRoot = _root };
        options.Ffmpeg.ExecutablePath = _ffmpeg!;
        options.Ffmpeg.ProbeExecutablePath = _ffprobe!;
        options.Ffmpeg.MaxFrameWidth = 320;
        options.Ffmpeg.MaxFrameHeight = 240;
        return options;
    }

    /// <summary>Builds fixture media directly; this is setup, not the code under test.</summary>
    private void Generate(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _ffmpeg!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(120_000);

        Assert.Equal(0, process.ExitCode);
    }

    private string GenerateVideo(string name = "clip.mp4", int seconds = 4)
    {
        var path = Path.Combine(_root, "video", name);
        Generate(
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc=size=640x480:rate=10:duration={seconds}",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", path);
        return path;
    }

    private string GenerateAudio(string name = "voice.wav", double seconds = 4)
    {
        var path = Path.Combine(_root, "audio", name);
        Generate(
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
            path);
        return path;
    }

    [Fact]
    public async Task Samples_real_frames_from_a_real_video()
    {
        if (!Enabled)
        {
            return;
        }

        GenerateVideo(seconds: 4);
        var options = CreateOptions();
        var runner = new FfmpegProcessRunner(Options.Create(options));
        var sampler = new FfmpegVideoFrameSampler(
            new MediaAssetPathResolver(Options.Create(options)),
            runner,
            Options.Create(options));

        var samples = await sampler.SampleAsync(
            new MediaAssetReference("asset://video/clip.mp4", "video/mp4"),
            "frames/smoke",
            new VideoFrameSamplingOptions
            {
                SampleInterval = TimeSpan.FromSeconds(1),
                MaxFrames = 10,
                MaxDuration = TimeSpan.FromSeconds(4)
            });

        // testsrc is a moving pattern, so dedup must not collapse it to one frame.
        Assert.InRange(samples.Count, 2, 10);
        Assert.All(samples, sample => Assert.False(Path.IsPathRooted(sample.RelativePath)));

        var written = Directory.GetFiles(Path.Combine(_root, "frames", "smoke"), "*.jpg");
        Assert.Equal(samples.Count, written.Length);

        // Real JPEG magic bytes: proves FFmpeg accepted the filter and encoder.
        var first = await File.ReadAllBytesAsync(
            Path.Combine(_root, "frames", "smoke", samples[0].RelativePath));
        Assert.True(first.Length > 0);
        Assert.Equal(0xFF, first[0]);
        Assert.Equal(0xD8, first[1]);
    }

    [Fact]
    public async Task Probes_real_durations()
    {
        if (!Enabled)
        {
            return;
        }

        var path = GenerateVideo("probe.mp4", seconds: 3);
        var options = CreateOptions();
        var probe = new FfprobeMediaProbe(
            new FfmpegProcessRunner(Options.Create(options)),
            Options.Create(options));

        var durations = await probe.ProbeAsync(path);

        Assert.NotNull(durations);
        Assert.NotNull(durations!.Video);
        Assert.InRange(durations.Video!.Value.TotalSeconds, 2.5, 3.5);
    }

    [Fact]
    public async Task Composes_real_media_and_returns_an_asset_uri()
    {
        if (!Enabled)
        {
            return;
        }

        GenerateVideo(seconds: 4);
        GenerateAudio(seconds: 4);

        var options = CreateOptions();
        var runner = new FfmpegProcessRunner(Options.Create(options));
        var resolver = new MediaAssetPathResolver(Options.Create(options));
        var service = new FfmpegVideoCompositionService(
            resolver,
            runner,
            new FfprobeMediaProbe(runner, Options.Create(options)),
            Options.Create(options));

        var result = await service.ComposeAsync(new MediaJobRequest(
            "smoke-compose",
            MediaJobKind.VideoComposition,
            [
                new MediaAssetReference("asset://video/clip.mp4", "video/mp4"),
                new MediaAssetReference("asset://audio/voice.wav", "audio/wav")
            ]));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        var asset = Assert.Single(result.Assets!);
        Assert.Equal("asset://media/smoke-compose.mp4", asset.Uri);

        var composed = Path.Combine(_root, "media", "smoke-compose.mp4");
        Assert.True(File.Exists(composed));
        Assert.True(new FileInfo(composed).Length > 0);

        // Drift is checked by the service; assert the composed file really carries both
        // streams so a silent single-stream pass cannot look like success.
        var durations = await new FfprobeMediaProbe(runner, Options.Create(options))
            .ProbeAsync(composed);
        Assert.NotNull(durations!.Video);
        Assert.NotNull(durations.Audio);
    }

    [Fact]
    public async Task Refuses_a_traversal_before_reaching_ffmpeg()
    {
        if (!Enabled)
        {
            return;
        }

        GenerateVideo();
        var options = CreateOptions();
        var sampler = new FfmpegVideoFrameSampler(
            new MediaAssetPathResolver(Options.Create(options)),
            new FfmpegProcessRunner(Options.Create(options)),
            Options.Create(options));

        var exception = await Assert.ThrowsAsync<MediaSamplingException>(
            () => sampler.SampleAsync(
                new MediaAssetReference("asset://video/../../windows/win.ini", "video/mp4"),
                "frames/blocked",
                new VideoFrameSamplingOptions()));

        Assert.Equal("unsafe_asset_reference", exception.FailureCode);
        Assert.False(Directory.Exists(Path.Combine(_root, "frames", "blocked")));
    }
}
