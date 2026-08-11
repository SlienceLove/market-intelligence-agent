using System.Net;
using System.Text;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class HttpFrameOcrServiceTests
{
    [Fact]
    public async Task Disabled_provider_returns_safe_failure_without_http_call()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var service = CreateService(handler, new OcrHttpOptions { Endpoint = "http://127.0.0.1:8092/v1/ocr" });

        var result = await service.RecognizeAsync(ValidRequest());

        Assert.Equal("provider_not_configured", result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Sends_canonical_request_and_normalizes_frames()
    {
        string? body = null;
        var handler = new RecordingHandler(async request =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Response(HttpStatusCode.OK, "{\"frames\":[" +
                "{\"timestampSeconds\":2,\"text\":\" later \",\"bounds\":{\"x\":10,\"y\":20,\"width\":100,\"height\":30},\"confidence\":1.4}," +
                "{\"timestampSeconds\":1,\"text\":\"first\",\"confidence\":-0.2}]}" );
        });
        var service = CreateService(handler, new OcrHttpOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8092/v1/ocr",
            ApiKey = "test-secret",
            ApiKeyHeaderName = "X-Provider-Key",
            InitialRetryDelay = TimeSpan.Zero
        });

        var result = await service.RecognizeAsync(ValidRequest() with
        {
            CorrelationId = "correlation-1",
            IdempotencyKey = "idempotency-1",
            Parameters = new Dictionary<string, string> { ["language"] = "zh" }
        });

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.Collection(result.OcrFrames!, first => Assert.Equal(0, first.Confidence), second =>
        {
            Assert.Equal(1, second.Confidence);
            Assert.Equal(100, second.Bounds!.Width);
        });
        using var document = JsonDocument.Parse(body!);
        Assert.Equal("asset://fixture/image", document.RootElement.GetProperty("input").GetProperty("uri").GetString());
        Assert.Equal("test-secret", handler.LastRequest!.Headers.GetValues("X-Provider-Key").Single());
        Assert.Equal("idempotency-1", handler.LastRequest.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task Retries_rate_limit_and_rejects_invalid_response_safely()
    {
        var count = 0;
        var retryHandler = new RecordingHandler(_ =>
        {
            count++;
            return Task.FromResult(count == 1
                ? Response(HttpStatusCode.TooManyRequests, "{}")
                : Response(HttpStatusCode.OK, "{\"frames\":[{\"timestampSeconds\":0,\"text\":\"ok\"}]}"));
        });
        var retryService = CreateService(retryHandler, new OcrHttpOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8092/v1/ocr",
            InitialRetryDelay = TimeSpan.Zero
        });
        var retryResult = await retryService.RecognizeAsync(ValidRequest());

        var invalidHandler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{\"frames\":[{\"text\":\"missing timestamp\"}]}")));
        var invalidService = CreateService(invalidHandler, new OcrHttpOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8092/v1/ocr"
        });
        var invalidResult = await invalidService.RecognizeAsync(ValidRequest() with { JobId = "invalid" });

        Assert.Equal(MediaJobStatus.Succeeded, retryResult.Status);
        Assert.Equal(2, retryHandler.CallCount);
        Assert.Equal("invalid_ocr_response", invalidResult.FailureCode);
    }

    [Fact]
    public async Task Reuses_ocr_input_policy_and_maps_cancellation()
    {
        var unsupported = ValidRequest() with
        {
            Inputs = [new MediaAssetReference("asset://fixture/document", "application/pdf", 1024)]
        };
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var service = CreateService(handler, new OcrHttpOptions
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8092/v1/ocr"
        });

        var unsupportedResult = await service.RecognizeAsync(unsupported);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledResult = await service.RecognizeAsync(ValidRequest() with { JobId = "cancelled" }, cancellation.Token);

        Assert.Equal("unsupported_ocr_format", unsupportedResult.FailureCode);
        Assert.Equal(MediaJobStatus.Cancelled, cancelledResult.Status);
        Assert.Equal(0, handler.CallCount);
    }

    private static HttpFrameOcrService CreateService(RecordingHandler handler, OcrHttpOptions options) =>
        new(new HttpClient(handler), Options.Create(options));

    private static MediaJobRequest ValidRequest() => new(
        "job-ocr-http",
        MediaJobKind.FrameOcr,
        [new MediaAssetReference("asset://fixture/image", "image/png", 1024)]);

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> onSend) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return onSend(request);
        }
    }
}
