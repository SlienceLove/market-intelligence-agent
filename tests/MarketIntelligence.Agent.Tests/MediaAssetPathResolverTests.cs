using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class MediaAssetPathResolverTests : IDisposable
{
    private readonly string _root;

    public MediaAssetPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mi-asset-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    private MediaAssetPathResolver CreateResolver(string? root = null) =>
        new(Options.Create(new MediaOptions { AssetRoot = root ?? _root }));

    // F-T01
    [Fact]
    public void Resolves_asset_inside_root_to_absolute_path()
    {
        Directory.CreateDirectory(Path.Combine(_root, "video"));
        var expected = Path.Combine(_root, "video", "demo.mp4");
        File.WriteAllText(expected, "x");

        var result = CreateResolver().ResolveInput("asset://video/demo.mp4");

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.FullPath);
        Assert.True(Path.IsPathRooted(result.FullPath));
    }

    [Fact]
    public void Resolves_fixture_scheme_as_well()
    {
        var result = CreateResolver().ResolveInput("fixture://video/demo.mp4");

        Assert.True(result.Succeeded);
        Assert.Equal(Path.Combine(_root, "video", "demo.mp4"), result.FullPath);
    }

    // F-T02
    [Theory]
    [InlineData("asset://video/../../escape.mp4")]
    [InlineData("asset://../escape.mp4")]
    [InlineData("asset://video/./demo.mp4")]
    [InlineData("asset://C:/windows/system32/cmd.exe")]
    [InlineData("asset://video/demo.mp4?token=abc")]
    [InlineData("asset://user:pass@video/demo.mp4")]
    public void Rejects_traversal_and_absolute_injection(string uri)
    {
        var result = CreateResolver().ResolveInput(uri);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureCode);
    }

    [Fact]
    public void Rejects_backslash_and_percent_encoding()
    {
        var resolver = CreateResolver();

        Assert.False(resolver.ResolveInput(@"asset://video\..\escape.mp4").Succeeded);
        Assert.Equal("unsafe_asset_reference", resolver.ResolveInput("asset://video/..%2f..%2fescape").FailureCode);
    }

    // F-T03
    [Fact]
    public void Rejects_leading_dash_segment_to_prevent_option_injection()
    {
        var result = CreateResolver().ResolveInput("asset://video/-vf");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    // F-T05
    [Theory]
    [InlineData("file:///tmp/video.mp4")]
    [InlineData("http://example.invalid/video.mp4")]
    [InlineData("temp://media/video.mp4")]
    [InlineData("data:video/mp4;base64,AAAA")]
    public void Rejects_schemes_outside_the_allowlist(string uri)
    {
        var result = CreateResolver().ResolveInput(uri);

        Assert.False(result.Succeeded);
        Assert.Equal("unsupported_source_uri", result.FailureCode);
    }

    [Fact]
    public void Rejects_reserved_device_names()
    {
        var result = CreateResolver().ResolveInput("asset://video/NUL");

        Assert.False(result.Succeeded);
        Assert.Equal("unsafe_asset_reference", result.FailureCode);
    }

    // F-T13
    [Fact]
    public void Reports_not_configured_when_root_is_missing_or_absent()
    {
        Assert.False(CreateResolver(root: "").IsConfigured);
        Assert.Equal("provider_not_configured", CreateResolver(root: "").ResolveInput("asset://video/demo.mp4").FailureCode);

        var missing = Path.Combine(_root, "does-not-exist");
        Assert.Equal("provider_not_configured", CreateResolver(missing).ResolveInput("asset://video/demo.mp4").FailureCode);
    }

    [Fact]
    public void Resolves_output_paths_and_rejects_escapes()
    {
        var resolver = CreateResolver();

        var ok = resolver.ResolveOutput("media/job-1.mp4");
        Assert.True(ok.Succeeded);
        Assert.Equal(Path.Combine(_root, "media", "job-1.mp4"), ok.FullPath);

        Assert.False(resolver.ResolveOutput("../outside.mp4").Succeeded);
        Assert.False(resolver.ResolveOutput("/etc/passwd").Succeeded);
        Assert.False(resolver.ResolveOutput(@"C:\windows\temp\x.mp4").Succeeded);
    }

    /// <summary>
    /// A sibling directory sharing the root's name prefix must not pass containment.
    /// </summary>
    [Fact]
    public void Rejects_sibling_directory_sharing_root_prefix()
    {
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);

        try
        {
            // Reaching the sibling requires a traversal, which is refused outright.
            var result = CreateResolver().ResolveOutput($"../{Path.GetFileName(sibling)}/x.mp4");

            Assert.False(result.Succeeded);
            Assert.Equal("unsafe_asset_reference", result.FailureCode);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }
}
