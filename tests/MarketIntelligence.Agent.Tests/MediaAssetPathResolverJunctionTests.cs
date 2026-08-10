using System.Diagnostics;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// F-T04 (junction variant). Directory junctions are the reparse point that matters most
/// for containment on Windows: unlike symbolic links they need no privilege at all, so an
/// unelevated process can plant one. That makes them the realistic escape, while the
/// symlink tests need Developer Mode or elevation to even run.
///
/// Verified on this machine: `mklink /J` succeeds with IsUserAnAdmin() == 0, and a file
/// outside the root is reachable through the junction.
/// </summary>
public sealed class MediaAssetPathResolverJunctionTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _outside;

    public MediaAssetPathResolverJunctionTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mi-asset-junction-tests", Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "root");
        _outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_base))
            {
                Directory.Delete(_base, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best-effort.
        }
    }

    private MediaAssetPathResolver CreateResolver() =>
        new(Options.Create(new MediaOptions { AssetRoot = _root }));

    [RequiresJunctionFact]
    public void Rejects_a_junction_escaping_the_root()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.mp4"), "secret");

        var link = Path.Combine(_root, "linked");
        JunctionSupport.Create(link, _outside);

        // Guard the premise: if the junction were not traversable the assertion below
        // would pass for the wrong reason.
        Assert.True(File.Exists(Path.Combine(link, "secret.mp4")));

        var result = CreateResolver().ResolveInput("asset://linked/secret.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    /// <summary>
    /// The escape does not need to be the last component before the file. A junction
    /// several levels up redirects everything beneath it, which is why containment has to
    /// resolve every ancestor rather than just the leaf's parent.
    /// </summary>
    [RequiresJunctionFact]
    public void Rejects_a_junction_nested_above_the_leaf()
    {
        var deepOutside = Path.Combine(_outside, "deep", "deeper");
        Directory.CreateDirectory(deepOutside);
        File.WriteAllText(Path.Combine(deepOutside, "secret.mp4"), "secret");

        var link = Path.Combine(_root, "linked");
        JunctionSupport.Create(link, _outside);

        var result = CreateResolver().ResolveInput("asset://linked/deep/deeper/secret.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    [RequiresJunctionFact]
    public void Allows_a_junction_that_stays_inside_the_root()
    {
        var inside = Path.Combine(_root, "real");
        Directory.CreateDirectory(inside);
        File.WriteAllText(Path.Combine(inside, "demo.mp4"), "ok");

        var link = Path.Combine(_root, "linked");
        JunctionSupport.Create(link, inside);

        var result = CreateResolver().ResolveInput("asset://linked/demo.mp4");

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// Output paths do not exist yet, so containment has to hold for a write target whose
    /// parent is a junction pointing out of the root.
    /// </summary>
    [RequiresJunctionFact]
    public void Rejects_an_output_path_under_an_escaping_junction()
    {
        var link = Path.Combine(_root, "linked");
        JunctionSupport.Create(link, _outside);

        var result = CreateResolver().ResolveOutput("linked/frames");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }
}

/// <summary>
/// Gates junction tests on the platform actually supporting them, reporting skipped
/// rather than passing vacuously. Junctions need no privilege, so on Windows this is
/// expected to run; it is the non-Windows case that skips.
/// </summary>
public sealed class RequiresJunctionFactAttribute : FactAttribute
{
    public RequiresJunctionFactAttribute()
    {
        Skip = JunctionSupport.SkipReason;
    }
}

internal static class JunctionSupport
{
    internal static string? SkipReason =>
        OperatingSystem.IsWindows() ? null : "Directory junctions are Windows-only.";

    /// <summary>
    /// Creates a junction with <c>mklink /J</c>. .NET has no junction API —
    /// <see cref="Directory.CreateSymbolicLink"/> makes a symlink, which needs privilege
    /// and so would not reproduce the unprivileged case being tested here.
    /// </summary>
    internal static void Create(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start cmd.exe to create a junction.");

        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mklink /J failed with exit code {process.ExitCode}: {error.Trim()}");
        }
    }
}
