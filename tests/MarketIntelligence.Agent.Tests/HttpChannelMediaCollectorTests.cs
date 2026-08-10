using System.Net;
using System.Net.Http.Headers;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;

namespace MarketIntelligence.Agent.Tests;

public sealed class HttpChannelMediaCollectorTests
{
    [Fact]
    public async Task CollectAsync_returns_registered_asset_metadata_for_allowlisted_source()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK,
            "video/mp4",
            [1, 2, 3, 4])));
        var collector = CreateCollector(handler);

        var result = await collector.CollectAsync(Request("collect-success"));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        var asset = Assert.Single(result.Assets!);
        Assert.Equal("https://approved.example/video", asset.Uri);
        Assert.Equal("video/mp4", asset.MediaType);
        Assert.Equal(4, asset.SizeBytes);
        Assert.Equal(1, handler.CallCount);
    }

    [Theory]
    [InlineData("file:///tmp/video.mp4", "unsupported_source_uri")]
    [InlineData("http://127.0.0.1/video", "private_source_uri")]
    [InlineData("https://unapproved.example/video", "source_host_not_allowed")]
    [InlineData("https://approved.example:8443/video", "source_port_not_allowed")]
    public async Task CollectAsync_rejects_unsafe_source_before_http_call(string source, string expectedCode)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK,
            "video/mp4",
            [1])));
        var collector = CreateCollector(handler);

        var result = await collector.CollectAsync(Request("collect-unsafe") with
        {
            Inputs = [new MediaAssetReference(source, "text/uri-list")]
        });

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "source_forbidden")]
    [InlineData((HttpStatusCode)429, "source_rate_limited")]
    [InlineData(HttpStatusCode.BadGateway, "source_server_error")]
    public async Task CollectAsync_maps_http_failures_to_stable_codes(HttpStatusCode status, string expectedCode)
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Response(status, "video/mp4", [])));
        var collector = CreateCollector(handler);

        var result = await collector.CollectAsync(Request("collect-status"));

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal(expectedCode, result.FailureCode);
    }

    [Fact]
    public async Task CollectAsync_rejects_oversized_body_and_unsupported_media_type()
    {
        var oversizedHandler = new StubHandler((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK,
            "video/mp4",
            [1, 2, 3, 4, 5])));
        var oversized = CreateCollector(oversizedHandler, options => options.MaxResponseBytes = 4);

        var oversizedResult = await oversized.CollectAsync(Request("collect-large"));

        Assert.Equal("source_response_too_large", oversizedResult.FailureCode);

        var typeHandler = new StubHandler((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK,
            "text/html",
            [1])));
        var wrongType = CreateCollector(typeHandler);

        var typeResult = await wrongType.CollectAsync(Request("collect-type"));

        Assert.Equal("source_media_type_not_allowed", typeResult.FailureCode);
    }

    [Fact]
    public async Task CollectAsync_maps_timeout_and_honors_cancellation()
    {
        var timeoutHandler = new StubHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return Response(HttpStatusCode.OK, "video/mp4", [1]);
        });
        var timeoutCollector = CreateCollector(timeoutHandler, options => options.Timeout = TimeSpan.FromMilliseconds(20));

        var timeoutResult = await timeoutCollector.CollectAsync(Request("collect-timeout"));

        Assert.Equal("source_timeout", timeoutResult.FailureCode);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledResult = await timeoutCollector.CollectAsync(
            Request("collect-cancelled"),
            cancellation.Token);

        Assert.Equal(MediaJobStatus.Cancelled, cancelledResult.Status);
    }

    [Fact]
    public async Task CollectAsync_validates_each_redirect_and_enforces_redirect_limit()
    {
        var redirectHandler = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = request.RequestUri!.AbsolutePath == "/video"
                ? new Uri("https://approved.example/next")
                : new Uri("https://unapproved.example/video");
            return Task.FromResult(response);
        });
        var redirectCollector = CreateCollector(redirectHandler);

        var redirectResult = await redirectCollector.CollectAsync(Request("collect-redirect"));

        Assert.Equal("source_host_not_allowed", redirectResult.FailureCode);
        Assert.Equal(2, redirectHandler.CallCount);

        var loopHandler = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new Uri(request.RequestUri!.AbsolutePath == "/video"
                ? "https://approved.example/next"
                : "https://approved.example/video");
            return Task.FromResult(response);
        });
        var loopCollector = CreateCollector(loopHandler, options => options.MaxRedirects = 1);

        var loopResult = await loopCollector.CollectAsync(Request("collect-redirect-limit"));

        Assert.Equal("source_redirect_limit_exceeded", loopResult.FailureCode);
    }

    [Fact]
    public async Task CollectAsync_reuses_idempotent_result_without_second_download()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(Response(
            HttpStatusCode.OK,
            "video/mp4",
            [1, 2])));
        var collector = CreateCollector(handler);
        var firstRequest = Request("collect-idempotent") with { IdempotencyKey = "same-request" };

        var first = await collector.CollectAsync(firstRequest);
        var retry = await collector.CollectAsync(firstRequest with { JobId = "different-job" });

        Assert.Equal(MediaJobStatus.Succeeded, first.Status);
        Assert.Equal(first, retry);
        Assert.Equal(1, handler.CallCount);
    }

    private static HttpChannelMediaCollector CreateCollector(
        HttpMessageHandler handler,
        Action<MediaCollectorOptions>? configure = null)
    {
        var options = new MediaCollectorOptions
        {
            AllowedHosts = ["approved.example"]
        };
        configure?.Invoke(options);
        return new HttpChannelMediaCollector(new HttpClient(handler), options);
    }

    private static MediaJobRequest Request(string jobId) => new(
        jobId,
        MediaJobKind.Collection,
        [new MediaAssetReference("https://approved.example/video", "text/uri-list")]);

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string mediaType,
        byte[] body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await responder(request, cancellationToken);
        }
    }
}
