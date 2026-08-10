using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// F-T04. Symbolic links are the one escape a lexical containment check cannot see,
/// so they get dedicated coverage.
///
/// Creating links needs privilege on Windows. Where it is unavailable these report as
/// skipped via <see cref="RequiresSymbolicLinkFactAttribute"/> rather than returning
/// early, because a green result from a test that created no link is indistinguishable
/// from one that verified containment. Link creation failing *inside* an enabled test
/// is a real failure, not a reason to bail.
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

    [RequiresSymbolicLinkFact]
    public void Rejects_file_link_escaping_the_root()
    {
        var target = Path.Combine(_outside, "secret.mp4");
        File.WriteAllText(target, "secret");

        var linkDirectory = Path.Combine(_root, "video");
        Directory.CreateDirectory(linkDirectory);
        var link = Path.Combine(linkDirectory, "demo.mp4");

        File.CreateSymbolicLink(link, target);

        var result = CreateResolver().ResolveInput("asset://video/demo.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    [RequiresSymbolicLinkFact]
    public void Rejects_directory_link_escaping_the_root()
    {
        File.WriteAllText(Path.Combine(_outside, "secret.mp4"), "secret");

        var link = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(link, _outside);

        var result = CreateResolver().ResolveInput("asset://linked/secret.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    [RequiresSymbolicLinkFact]
    public void Allows_link_that_stays_inside_the_root()
    {
        var inside = Path.Combine(_root, "real");
        Directory.CreateDirectory(inside);
        var target = Path.Combine(inside, "demo.mp4");
        File.WriteAllText(target, "ok");

        var linkDirectory = Path.Combine(_root, "video");
        Directory.CreateDirectory(linkDirectory);
        var link = Path.Combine(linkDirectory, "demo.mp4");

        File.CreateSymbolicLink(link, target);

        var result = CreateResolver().ResolveInput("asset://video/demo.mp4");

        Assert.True(result.Succeeded);
    }

    /// <summary>
    /// A link chain that never terminates must fail closed. Before the resolver threw on
    /// depth exhaustion it returned the last hop, so an attacker-built chain resolved to
    /// an unverified path that then passed the containment check.
    /// </summary>
    [RequiresSymbolicLinkFact]
    public void Rejects_a_link_cycle_rather_than_resolving_it()
    {
        var first = Path.Combine(_root, "a");
        var second = Path.Combine(_root, "b");

        Directory.CreateSymbolicLink(first, second);
        Directory.CreateSymbolicLink(second, first);

        var result = CreateResolver().ResolveInput("asset://a/demo.mp4");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }
}
