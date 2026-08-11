using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>F-T06 through F-T08.</summary>
public sealed class FfmpegProcessRunnerTests : IDisposable
{
    private readonly string _workspace;

    public FfmpegProcessRunnerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "mi-runner-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
            {
                Directory.Delete(_workspace, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private static FfmpegProcessRunner CreateRunner(
        string? executable = null,
        string? probeExecutable = null,
        int maxStandardErrorBytes = 4 * 1024)
    {
        var options = new MediaOptions();
        options.Ffmpeg.ExecutablePath = executable ?? ProcessProbeShell.ExecutablePath;
        options.Ffmpeg.ProbeExecutablePath = probeExecutable ?? ProcessProbeShell.ExecutablePath;
        options.Ffmpeg.MaxStandardErrorBytes = maxStandardErrorBytes;
        return new FfmpegProcessRunner(Options.Create(options));
    }

    private static ProcessRunRequest Request(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        string fileName = "ffmpeg") =>
        new(fileName, arguments, timeout ?? TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Reports_provider_not_configured_when_path_is_empty()
    {
        var options = new MediaOptions();
        options.Ffmpeg.ExecutablePath = string.Empty;
        var runner = new FfmpegProcessRunner(Options.Create(options));

        var result = await runner.RunAsync(Request(ProcessProbeShell.ExitWith(0)));

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("provider_not_configured", result.ErrorSummary);
    }

    [Fact]
    public async Task Reports_provider_not_configured_when_path_does_not_exist()
    {
        var missing = Path.Combine(_workspace, "no-such-ffmpeg.exe");
        var runner = CreateRunner(executable: missing);

        var result = await runner.RunAsync(Request(ProcessProbeShell.ExitWith(0)));

        Assert.Equal("provider_not_configured", result.ErrorSummary);
    }

    [Fact]
    public async Task Never_falls_back_to_an_executable_on_the_path()
    {
        // A bare file name must not be resolved through PATH; the configured absolute
        // path is the only executable the runner is allowed to launch.
        var runner = CreateRunner(executable: "cmd.exe");

        var result = await runner.RunAsync(Request(ProcessProbeShell.ExitWith(0)));

        Assert.Equal("provider_not_configured", result.ErrorSummary);
    }

    [Fact]
    public async Task Returns_zero_exit_code_and_stdout_on_success()
    {
        if (!ProcessProbeShell.IsAvailable)
        {
            return;
        }

        var runner = CreateRunner();

        var result = await runner.RunAsync(Request(ProcessProbeShell.EchoStdout("marker-value")));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Contains("marker-value", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Surfaces_a_nonzero_exit_code()
    {
        if (!ProcessProbeShell.IsAvailable)
        {
            return;
        }

        var runner = CreateRunner();

        var result = await runner.RunAsync(Request(ProcessProbeShell.ExitWith(3)));

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task Routes_ffprobe_to_the_probe_executable()
    {
        if (!ProcessProbeShell.IsAvailable)
        {
            return;
        }

        var runner = CreateRunner(executable: Path.Combine(_workspace, "missing-ffmpeg.exe"));

        var result = await runner.RunAsync(
            Request(ProcessProbeShell.EchoStdout("probe-ran"), fileName: "ffprobe"));

        // ffmpeg is deliberately unconfigured, so a success here proves the probe path
        // was selected rather than the ffmpeg path.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("probe-ran", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caps_the_error_summary_and_still_lets_the_process_exit()
    {
        if (!ProcessProbeShell.IsAvailable)
        {
            return;
        }

        const int budget = 512;
        var runner = CreateRunner(maxStandardErrorBytes: budget);

        var result = await runner.RunAsync(
            Request(ProcessProbeShell.FloodStderr(2_000), timeout: TimeSpan.FromMinutes(2)));

        // Exit code 0 is the real assertion: the child emitted far more stderr than the
        // budget and still finished, so draining continued past the capture ceiling
        // instead of blocking the child on a full pipe.
        Assert.Equal(0, result.ExitCode);
        Assert.True(
            result.ErrorSummary.Length <= budget,
            $"Expected summary within {budget} characters but got {result.ErrorSummary.Length}.");
        Assert.DoesNotContain('\n', result.ErrorSummary);
        Assert.DoesNotContain('\r', result.ErrorSummary);
    }

    [Fact]
    public async Task Times_out_and_kills_the_child()
    {
        if (!ProcessProbeShell.IsAvailable)
        {
            return;
        }

        var marker = Path.Combine(_workspace, "ticks.txt");
        var runner = CreateRunner();

        var result = await runner.RunAsync(
            Request(ProcessProbeShell.AppendForever(marker), timeout: TimeSpan.FromSeconds(2)));

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal(-1, result.ExitCode);

        // If the child survived the timeout it would still be appending. A file that
        // stops growing is the observable proof the tree kill landed.
        var firstLength = SafeLength(marker);
        await Task.Delay(TimeSpan.FromSeconds(2));
        var secondLength = SafeLength(marker);

        Assert.Equal(firstLength, secondLength);
    }

    [Fact]
    public async Task Reports_cancellation_distinctly_from_timeout()
    {
        if (!ProcessProbeShell.IsAvailable)
        {
            return;
        }

        var marker = Path.Combine(_workspace, "cancel-ticks.txt");
        var runner = CreateRunner();
        using var source = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await runner.RunAsync(
            Request(ProcessProbeShell.AppendForever(marker), timeout: TimeSpan.FromMinutes(2)),
            source.Token);

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Equal("cancelled", result.ErrorSummary);
    }

    [Fact]
    public async Task Returns_cancelled_without_starting_when_already_cancelled()
    {
        var runner = CreateRunner();
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        var result = await runner.RunAsync(Request(ProcessProbeShell.ExitWith(0)), source.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return -1;
        }
    }
}
