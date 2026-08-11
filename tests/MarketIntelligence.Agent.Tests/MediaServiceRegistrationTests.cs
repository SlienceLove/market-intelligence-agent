using MarketIntelligence.Agent.Application;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarketIntelligence.Agent.Tests;

/// <summary>
/// F-T13. The container must hand out the FFmpeg implementations and must still build
/// when FFmpeg is not configured at all, so an unconfigured host starts and reports
/// provider_not_configured instead of failing at resolution.
/// </summary>
public sealed class MediaServiceRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // BindConfiguration needs IConfiguration in the container. Empty on purpose:
        // this container stands in for a host with no media settings supplied.
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection().Build());

        // Same order as Program.cs: infrastructure registers last and therefore wins.
        services.AddApplication();
        services.AddInfrastructure();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void Resolves_the_ffmpeg_backed_implementations()
    {
        using var provider = BuildProvider();

        Assert.IsType<MediaAssetPathResolver>(provider.GetRequiredService<IMediaAssetPathResolver>());
        Assert.IsType<FfmpegProcessRunner>(provider.GetRequiredService<IProcessRunner>());
        Assert.IsType<FfprobeMediaProbe>(provider.GetRequiredService<IMediaProbe>());
        Assert.IsType<FfmpegVideoFrameSampler>(provider.GetRequiredService<IVideoFrameSampler>());
    }

    [Fact]
    public void Resolves_the_unconfigured_speech_service_by_default()
    {
        using var provider = BuildProvider();

        Assert.IsType<UnconfiguredSpeechSynthesisService>(provider.GetRequiredService<ISpeechSynthesisService>());
    }

    [Fact]
    public void Resolves_the_http_speech_service_when_enabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Media:Tts:Enabled"] = "true",
            ["Media:Tts:Endpoint"] = "http://127.0.0.1:8093/v1/speech-synthesis",
            ["Media:Tts:ServiceKey"] = "test-secret",
            ["Media:AssetRoot"] = Path.GetTempPath()
        }).Build());
        services.AddApplication();
        services.AddInfrastructure();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        var first = provider.GetRequiredService<ISpeechSynthesisService>();
        var second = provider.GetRequiredService<ISpeechSynthesisService>();

        Assert.IsType<HttpSpeechSynthesisService>(first);
        Assert.IsType<HttpSpeechSynthesisService>(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Prefers_the_real_composition_service_over_the_unconfigured_placeholder()
    {
        using var provider = BuildProvider();

        var composition = provider.GetRequiredService<IVideoCompositionService>();

        Assert.IsType<FfmpegVideoCompositionService>(composition);
    }

    [Fact]
    public void Reports_not_configured_rather_than_throwing_when_ffmpeg_is_absent()
    {
        using var provider = BuildProvider();

        var resolver = provider.GetRequiredService<IMediaAssetPathResolver>();

        // No AssetRoot is configured in this container, so nothing resolves and no
        // filesystem access is attempted.
        Assert.False(resolver.IsConfigured);
        Assert.Equal("provider_not_configured", resolver.ResolveInput("asset://video/demo.mp4").FailureCode);
    }

    [Fact]
    public async Task Unconfigured_composition_fails_cleanly_through_the_container()
    {
        using var provider = BuildProvider();
        var composition = provider.GetRequiredService<IVideoCompositionService>();

        var result = await composition.ComposeAsync(new MediaJobRequest(
            "job-di-check",
            MediaJobKind.VideoComposition,
            [
                new MediaAssetReference("asset://video/clip.mp4", "video/mp4"),
                new MediaAssetReference("asset://audio/voice.wav", "audio/wav")
            ]));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("provider_not_configured", result.FailureCode);
    }
}
