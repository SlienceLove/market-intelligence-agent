using System.Diagnostics;
using System.Text;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Launches FFmpeg without a shell. Arguments are passed through
/// <see cref="ProcessStartInfo.ArgumentList"/> so the OS receives them as a vector and
/// no quoting or escaping rules are applied to caller-influenced values.
/// </summary>
public sealed class FfmpegProcessRunner : IProcessRunner
{
    // ffprobe JSON for format plus streams stays far below this; ffmpeg writes media
    // to the output file rather than stdout, so a small ceiling is safe for both.
    private const int MaxStandardOutputCharacters = 64 * 1024;

    private readonly MediaOptions _options;

    public FfmpegProcessRunner(IOptions<MediaOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ProcessRunResult> RunAsync(
        ProcessRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return new ProcessRunResult(-1, "cancelled", TimeSpan.Zero, Cancelled: true);
        }

        var executable = ResolveExecutable(request.FileName);
        if (executable is null)
        {
            return new ProcessRunResult(-1, "provider_not_configured", TimeSpan.Zero);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new ProcessRunResult(-1, "process_start_failed", stopwatch.Elapsed);
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessRunResult(-1, "process_start_failed", stopwatch.Elapsed);
        }

        var errorBudget = _options.Ffmpeg.MaxStandardErrorBytes > 0
            ? _options.Ffmpeg.MaxStandardErrorBytes
            : 4 * 1024;

        // Both pipes must be drained continuously. Waiting for exit while a pipe
        // buffer is full deadlocks the child process.
        var errorTask = ReadBoundedAsync(process.StandardError, errorBudget);
        var outputTask = ReadBoundedAsync(process.StandardOutput, MaxStandardOutputCharacters);

        using var timeoutSource = new CancellationTokenSource();
        if (request.Timeout > TimeSpan.Zero && request.Timeout != Timeout.InfiniteTimeSpan)
        {
            timeoutSource.CancelAfter(request.Timeout);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            var cancelledByCaller = cancellationToken.IsCancellationRequested;
            KillProcessTree(process);

            // Give the pipes a bounded chance to flush so the summary is still useful.
            var summary = await SafeAwaitAsync(errorTask);
            await SafeAwaitAsync(outputTask);

            return cancelledByCaller
                ? new ProcessRunResult(-1, Truncate("cancelled", errorBudget), stopwatch.Elapsed, Cancelled: true)
                : new ProcessRunResult(-1, Truncate(summary, errorBudget), stopwatch.Elapsed, TimedOut: true);
        }

        var errorSummary = await SafeAwaitAsync(errorTask);
        var standardOutput = await SafeAwaitAsync(outputTask);
        stopwatch.Stop();

        return new ProcessRunResult(
            process.ExitCode,
            Truncate(errorSummary, errorBudget),
            stopwatch.Elapsed,
            StandardOutput: standardOutput);
    }

    private string? ResolveExecutable(string fileName)
    {
        var configured = string.Equals(fileName, "ffprobe", StringComparison.OrdinalIgnoreCase)
            ? _options.Ffmpeg.ProbeExecutablePath
            : _options.Ffmpeg.ExecutablePath;

        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        string full;
        try
        {
            full = Path.GetFullPath(configured);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return File.Exists(full) ? full : null;
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Bounded reap so a killed child does not linger as a zombie.
                process.WaitForExit(5_000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process already exited or cannot be signalled; nothing to reclaim.
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes)
    {
        var builder = new StringBuilder();
        var buffer = new char[1024];
        var captured = 0;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            if (captured < maxBytes)
            {
                var take = Math.Min(read, maxBytes - captured);
                builder.Append(buffer, 0, take);
                captured += take;
            }

            // Keep draining past the budget: stop reading and the child blocks.
        }

        return builder.ToString();
    }

    private static async Task<string> SafeAwaitAsync(Task<string> task)
    {
        try
        {
            return await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Collapses FFmpeg diagnostics to a bounded, single-line summary. Absolute paths
    /// and separators are stripped so log sinks never receive filesystem layout.
    /// </summary>
    private static string Truncate(string value, int maxCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var flattened = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\\', '/')
            .Trim();

        return flattened.Length <= maxCharacters ? flattened : flattened[..maxCharacters];
    }
}
