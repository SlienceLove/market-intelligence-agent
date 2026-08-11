using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class FfprobeMediaProbeTests
{
    private sealed class CannedRunner : IProcessRunner
    {
        private readonly ProcessRunResult _result;

        public CannedRunner(string stdout, ProcessRunResult? result = null)
        {
            _result = result ?? new ProcessRunResult(
                0,
                string.Empty,
                TimeSpan.FromMilliseconds(5),
                StandardOutput: stdout);
        }

        public string LastFileName { get; private set; } = string.Empty;

        public Task<ProcessRunResult> RunAsync(
            ProcessRunRequest request,
            CancellationToken cancellationToken = default)
        {
            LastFileName = request.FileName;
            return Task.FromResult(_result);
        }
    }

    private static FfprobeMediaProbe CreateProbe(IProcessRunner runner, string probePath = "ffprobe.exe")
    {
        var options = new MediaOptions();
        options.Ffmpeg.ProbeExecutablePath = probePath;
        return new FfprobeMediaProbe(runner, Options.Create(options));
    }

    [Fact]
    public async Task Parses_container_and_stream_durations()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "duration": "12.500000" },
            { "codec_type": "audio", "duration": "12.300000" }
          ],
          "format": { "duration": "12.520000" }
        }
        """;
        var runner = new CannedRunner(json);

        var durations = await CreateProbe(runner).ProbeAsync("C:/media/out.mp4");

        Assert.NotNull(durations);
        Assert.Equal(12.52, durations!.Container!.Value.TotalSeconds, 3);
        Assert.Equal(12.5, durations.Video!.Value.TotalSeconds, 3);
        Assert.Equal(12.3, durations.Audio!.Value.TotalSeconds, 3);
        Assert.Equal(0.2, durations.Drift!.Value.TotalSeconds, 3);

        // Routing matters: the runner keys the probe executable off this name.
        Assert.Equal("ffprobe", runner.LastFileName);
    }

    [Fact]
    public async Task Keeps_the_longest_stream_of_each_type()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "duration": "5.0" },
            { "codec_type": "video", "duration": "9.0" },
            { "codec_type": "audio", "duration": "4.0" },
            { "codec_type": "audio", "duration": "8.0" }
          ]
        }
        """;

        var durations = await CreateProbe(new CannedRunner(json)).ProbeAsync("C:/media/out.mp4");

        Assert.Equal(9.0, durations!.Video!.Value.TotalSeconds, 3);
        Assert.Equal(8.0, durations.Audio!.Value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("""{ "streams": [ { "codec_type": "video", "duration": "N/A" } ] }""")]
    [InlineData("""{ "streams": [ { "codec_type": "video" } ] }""")]
    [InlineData("""{ "streams": [] }""")]
    public async Task Reports_no_drift_when_a_duration_is_unavailable(string json)
    {
        var durations = await CreateProbe(new CannedRunner(json)).ProbeAsync("C:/media/out.mp4");

        Assert.NotNull(durations);
        Assert.Null(durations!.Drift);
    }

    [Fact]
    public async Task Returns_null_on_unparseable_output()
    {
        var durations = await CreateProbe(new CannedRunner("not json at all")).ProbeAsync("C:/media/out.mp4");

        Assert.Null(durations);
    }

    [Fact]
    public async Task Returns_null_when_ffprobe_fails()
    {
        var runner = new CannedRunner(
            string.Empty,
            new ProcessRunResult(1, "probe error", TimeSpan.FromMilliseconds(5)));

        Assert.Null(await CreateProbe(runner).ProbeAsync("C:/media/out.mp4"));
    }

    [Fact]
    public async Task Returns_null_when_not_configured()
    {
        var probe = CreateProbe(new CannedRunner("{}"), probePath: string.Empty);

        Assert.False(probe.IsConfigured);
        Assert.Null(await probe.ProbeAsync("C:/media/out.mp4"));
    }

    [Fact]
    public void Drift_is_absolute()
    {
        var audioLonger = new MediaDurations(null, TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));
        var videoLonger = new MediaDurations(null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(9));

        Assert.Equal(TimeSpan.FromSeconds(1), audioLonger.Drift);
        Assert.Equal(TimeSpan.FromSeconds(1), videoLonger.Drift);
    }
}
