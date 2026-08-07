using System.Net;
using System.Net.Http.Json;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Application.Images;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MarketIntelligence.Agent.Tests;

public sealed class ImageGenerationEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public ImageGenerationEndpointTests(WebApplicationFactory<Program> factory) =>
        _client = factory.CreateClient();

    [Fact]
    public async Task GenerateImage_requires_service_key_before_calling_comfyui()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/image/generate",
            new ImageGenerationRequest("test prompt"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Media_contract_rejects_empty_input_without_provider_call()
    {
        var request = new MediaJobRequest("job-1", MediaJobKind.Transcription, []);

        Assert.Equal("input_asset_required", request.Validate());
        var service = new UnconfiguredTranscriptionService();
        var result = await service.TranscribeAsync(request);

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("provider_not_configured", result.FailureCode);
    }

    [Fact]
    public async Task Unconfigured_media_service_returns_safe_failure()
    {
        var request = new MediaJobRequest(
            "job-2",
            MediaJobKind.VideoComposition,
            [new MediaAssetReference("asset://fixture/video", "video/mp4")]);

        var result = await new UnconfiguredVideoCompositionService().ComposeAsync(request);

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("provider_not_configured", result.FailureCode);
        Assert.DoesNotContain("asset://", result.FailureMessage);
    }

    [Theory]
    [InlineData("file:///tmp/video.mp4", "unsupported_source_uri")]
    [InlineData("http://127.0.0.1/video", "private_source_uri")]
    [InlineData("https://unauthorized.example/video", "source_host_not_allowed")]
    [InlineData("https://user:pass@approved.example/video", "private_source_uri")]
    public void Source_uri_policy_rejects_unsafe_sources(string source, string expectedCode)
    {
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" };

        var valid = MediaSourceUriPolicy.TryValidate(source, allowedHosts, out _, out var failureCode);

        Assert.False(valid);
        Assert.Equal(expectedCode, failureCode);
    }

    [Fact]
    public void Source_uri_policy_accepts_allowlisted_https_host()
    {
        var allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" };

        var valid = MediaSourceUriPolicy.TryValidate(
            "https://approved.example/video?id=fixture",
            allowedHosts,
            out var uri,
            out var failureCode);

        Assert.True(valid);
        Assert.Equal("https", uri?.Scheme);
        Assert.Null(failureCode);
    }

    [Fact]
    public async Task Fake_collector_returns_fixture_asset_and_reuses_idempotency_key()
    {
        var collector = new FakeChannelMediaCollector(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" });
        var first = new MediaJobRequest(
            "job-3",
            MediaJobKind.Collection,
            [new MediaAssetReference("https://approved.example/video", "text/uri-list")],
            IdempotencyKey: "request-1");
        var retry = first with { JobId = "job-4" };

        var firstResult = await collector.CollectAsync(first);
        var retryResult = await collector.CollectAsync(retry);

        Assert.Equal(MediaJobStatus.Succeeded, firstResult.Status);
        Assert.Equal(firstResult, retryResult);
        Assert.StartsWith("asset://fixture/", firstResult.Assets![0].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fake_collector_does_not_collect_unsafe_or_cancelled_source()
    {
        var collector = new FakeChannelMediaCollector(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" });
        var unsafeRequest = new MediaJobRequest(
            "job-5",
            MediaJobKind.Collection,
            [new MediaAssetReference("http://127.0.0.1/video", "text/uri-list")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var unsafeResult = await collector.CollectAsync(unsafeRequest);
        var cancelledResult = await collector.CollectAsync(unsafeRequest with { JobId = "job-6" }, cancellation.Token);

        Assert.Equal("private_source_uri", unsafeResult.FailureCode);
        Assert.Empty(unsafeResult.Assets ?? []);
        Assert.Equal(MediaJobStatus.Cancelled, cancelledResult.Status);
    }

    [Fact]
    public async Task Fake_transcription_normalizes_order_overlap_and_confidence()
    {
        var service = new FakeTranscriptionService(
        [
            new TimedTextSegment(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), " second ", 1.4),
            new TimedTextSegment(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2.5), "first", -0.2)
        ]);
        var request = new MediaJobRequest(
            "job-7",
            MediaJobKind.Transcription,
            [new MediaAssetReference("asset://fixture/audio", "audio/wav", 1024, TimeSpan.FromSeconds(5))]);

        var result = await service.TranscribeAsync(request);

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.Collection(
            result.TimedText!,
            first =>
            {
                Assert.Equal(TimeSpan.FromSeconds(1), first.Start);
                Assert.Equal(0, first.Confidence);
            },
            second =>
            {
                Assert.Equal(TimeSpan.FromSeconds(2.5), second.Start);
                Assert.Equal(1, second.Confidence);
            });
    }

    [Fact]
    public async Task Fake_transcription_rejects_empty_result_and_oversized_audio()
    {
        var empty = new FakeTranscriptionService([]);
        var request = new MediaJobRequest(
            "job-8",
            MediaJobKind.Transcription,
            [new MediaAssetReference("asset://fixture/audio", "audio/wav", 1024, TimeSpan.FromSeconds(5))]);
        var oversized = request with
        {
            JobId = "job-9",
            Inputs = [new MediaAssetReference("asset://fixture/audio", "audio/wav", 60 * 1024 * 1024, TimeSpan.FromSeconds(5))]
        };

        var emptyResult = await empty.TranscribeAsync(request);
        var oversizedResult = await new FakeTranscriptionService(
            [new TimedTextSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "ok")]).TranscribeAsync(oversized);

        Assert.Equal("empty_transcript", emptyResult.FailureCode);
        Assert.Equal("audio_size_exceeded", oversizedResult.FailureCode);
    }
}
