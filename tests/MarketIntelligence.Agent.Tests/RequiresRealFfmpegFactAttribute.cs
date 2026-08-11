using Xunit;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// Marks a test that needs the real FFmpeg binaries, and reports it as *skipped*
/// rather than passed when they are absent.
///
/// The distinction matters more than it looks. An early <c>return</c> inside the test
/// body also avoids the missing binary, but the test then reports green — so a run
/// where nothing was exercised is indistinguishable from a run that verified real
/// encoding. Setting <see cref="FactAttribute.Skip"/> instead makes the inert case
/// visible in the summary as a skip count.
///
/// xUnit 2.5.3 has no dynamic skip API (no <c>Assert.Skip</c>), but <c>Skip</c> is
/// evaluated per discovered test, so deciding it in the constructor works.
///
/// Configure with:
///   MI_SMOKE_FFMPEG=&lt;path to ffmpeg&gt;  MI_SMOKE_FFPROBE=&lt;path to ffprobe&gt;
/// </summary>
public sealed class RequiresRealFfmpegFactAttribute : FactAttribute
{
    public RequiresRealFfmpegFactAttribute()
    {
        Skip = ResolveSkipReason();
    }

    internal static string? ResolveSkipReason()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("MI_SMOKE_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("MI_SMOKE_FFPROBE");

        if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(ffprobe))
        {
            return "MI_SMOKE_FFMPEG / MI_SMOKE_FFPROBE not set; real-binary smoke skipped.";
        }

        // A configured-but-wrong path is worth naming precisely: it usually means a
        // stale install or a WinGet shim rather than a deliberate opt-out.
        if (!File.Exists(ffmpeg))
        {
            return $"MI_SMOKE_FFMPEG does not point at an existing file: {ffmpeg}";
        }

        return !File.Exists(ffprobe)
            ? $"MI_SMOKE_FFPROBE does not point at an existing file: {ffprobe}"
            : null;
    }
}
