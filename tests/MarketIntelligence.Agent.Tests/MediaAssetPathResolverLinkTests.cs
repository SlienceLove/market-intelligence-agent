using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// F-T04. Symbolic links are the one escape a lexical containment check cannot see,
/// so it gets dedicated coverage. Creating links needs privilege on Windows; when
/// that is unavailable the test reports inconclusive rather than failing, so the
/// suite stays runnable on unprivileged machines.
/// </summary>
public sealed class MediaAssetPathResolverLinkTests : IDisposable
{
    private readonly string _base;
    private readonly string _root;
    private readonly string _outside;

    public MediaAssetPathResolverLinkTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "mi-asset-link-tests", Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void Rejects_file_link_escaping_the_root()
    {
        var target = Path.Combine(_outside, "secret.mp4");
        File.WriteAllText(target, "secret");

        var linkDirectory = Path.Combine(_root, "video");
        Directory.CreateDirectory(linkDirectory);
        var link = Path.Combine(linkDirectory, "demo.mp4");

        if (!TryCreateFileLink(link, target))
        {
            return;
        }

        var result = CreateResolver().ResolveInput("asset://video/demo.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    [Fact]
    public void Rejects_directory_link_escaping_the_root()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.mp4"), "secret");

        var link = Path.Combine(_root, "linked");
        if (!TryCreateDirectoryLink(link, _outside))
        {
            return;
        }

        var result = CreateResolver().ResolveInput("asset://linked/secret.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    [Fact]
    public void Allows_link_that_stays_inside_the_root()
    {
        var inside = Path.Combine(_root, "real");
        Directory.CreateDirectory(inside);
        var target = Path.Combine(inside, "demo.mp4");
        File.WriteAllText(target, "ok");

        var linkDirectory = Path.Combine(_root, "video");
        Directory.CreateDirectory(linkDirectory);
        var link = Path.Combine(linkDirectory, "demo.mp4");

        if (!TryCreateFileLink(link, target))
        {
            return;
        }

        var result = CreateResolver().ResolveInput("asset://video/demo.mp4");

        Assert.True(result.Succeeded);
    }

    private static bool TryCreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
