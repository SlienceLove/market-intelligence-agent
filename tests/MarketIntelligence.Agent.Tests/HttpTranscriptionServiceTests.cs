using System.Net;
using System.Text;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class HttpTranscriptionServiceTests
{
    [Fact]
    public async Task Disabled_provider_returns_safe_failure_without_http_call()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var service = CreateService(handler, new AsrHttpOptions
        {
            Endpoint = "https://provider.example/transcribe"
        });

        var result = await service.TranscribeAsync(ValidRequest());

        Assert.Equal("provider_not_configured", result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Sends_canonical_request_and_normalizes_provider_segments()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return Response(
                HttpStatusCode.OK,
                "{\"segments\":[" +
                "{\"startSeconds\":2,\"endSeconds\":3,\"text\":\" second \",\"confidence\":1.4}," +
                "{\"startSeconds\":1,\"endSeconds\":2.5,\"text\":\"first\",\"confidence\":-0.2}" +
                "]}");
        });
        var service = CreateService(handler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe",
            ApiKey = "test-secret",
            ApiKeyHeaderName = "X-Provider-Key",
            Model = "configured-model",
            InitialRetryDelay = TimeSpan.Zero,
            Transcription = new TranscriptionOptions
            {
                MaxSegmentCharacters = 100,
                MaxTotalCharacters = 1000
            }
        });

        var result = await service.TranscribeAsync(ValidRequest() with
        {
            CorrelationId = "correlation-1",
            IdempotencyKey = "idempotency-1",
            Parameters = new Dictionary<string, string> { ["language"] = "zh" }
        });

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

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody!);
        Assert.Equal("job-http", document.RootElement.GetProperty("jobId").GetString());
        Assert.Equal(
            "asset://fixture/audio",
            document.RootElement.GetProperty("input").GetProperty("uri").GetString());
        Assert.Equal("configured-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("test-secret", handler.LastRequest!.Headers.GetValues("X-Provider-Key").Single());
        Assert.Equal("idempotency-1", handler.LastRequest.Headers.GetValues("Idempotency-Key").Single());
    }

    [Fact]
    public async Task Retries_rate_limit_and_transient_provider_errors()
    {
        var handlerCallCount = 0;
        var handler = new RecordingHandler(request =>
        {
            handlerCallCount++;
            return Task.FromResult(handlerCallCount switch
            {
                1 => Response(HttpStatusCode.TooManyRequests, "{}"),
                2 => Response(HttpStatusCode.ServiceUnavailable, "{}"),
                _ => Response(HttpStatusCode.OK, "{\"segments\":[{\"start\":0,\"end\":1,\"text\":\"ok\"}]}")
            });
        });

        var service = CreateService(handler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe",
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.Zero
        });

        var result = await service.TranscribeAsync(ValidRequest());

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Exhausted_rate_limit_is_classified_without_retrying_forever()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{}")));
        var service = CreateService(handler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe",
            MaxAttempts = 2,
            InitialRetryDelay = TimeSpan.Zero
        });

        var result = await service.TranscribeAsync(ValidRequest());

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("asr_rate_limited", result.FailureCode);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Non_retryable_http_errors_are_classified()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.BadRequest, "provider details are not returned")));
        var service = CreateService(handler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe"
        });

        var result = await service.TranscribeAsync(ValidRequest());

        Assert.Equal("asr_invalid_request", result.FailureCode);
        Assert.DoesNotContain("provider details", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Invalid_response_and_oversized_response_fail_safely()
    {
        var invalidHandler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{\"segments\":[{\"text\":\"missing times\"}]}")));
        var invalidService = CreateService(invalidHandler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe"
        });
        var invalid = await invalidService.TranscribeAsync(ValidRequest());

        var oversizedHandler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "123456789")));
        var oversizedService = CreateService(oversizedHandler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe",
            MaxResponseBytes = 4
        });
        var oversized = await oversizedService.TranscribeAsync(ValidRequest());

        Assert.Equal("invalid_provider_response", invalid.FailureCode);
        Assert.Equal("asr_response_too_large", oversized.FailureCode);
    }

    [Fact]
    public async Task Reuses_input_policy_and_maps_timeout_and_cancellation()
    {
        var unsupported = ValidRequest() with
        {
            Inputs = [new MediaAssetReference("asset://fixture/document", "application/pdf", 1024, TimeSpan.FromSeconds(5))]
        };
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("provider timeout"));
        var service = CreateService(handler, new AsrHttpOptions
        {
            Enabled = true,
            Endpoint = "https://provider.example/transcribe",
            MaxAttempts = 1
        });

        var unsupportedResult = await service.TranscribeAsync(unsupported);
        var timeoutResult = await service.TranscribeAsync(ValidRequest() with { JobId = "timeout" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelledResult = await service.TranscribeAsync(ValidRequest() with { JobId = "cancelled" }, cancellation.Token);

        Assert.Equal("unsupported_audio_format", unsupportedResult.FailureCode);
        Assert.Equal("asr_timeout", timeoutResult.FailureCode);
        Assert.Equal(MediaJobStatus.Cancelled, cancelledResult.Status);
    }

    private static HttpTranscriptionService CreateService(
        RecordingHandler handler,
        AsrHttpOptions options)
    {
        return new HttpTranscriptionService(
            new HttpClient(handler),
            Options.Create(options));
    }

    private static MediaJobRequest ValidRequest() => new(
        "job-http",
        MediaJobKind.Transcription,
        [new MediaAssetReference(
            "asset://fixture/audio",
            "audio/wav",
            1024,
            TimeSpan.FromSeconds(5))]);

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>>? onSend) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, Task<HttpResponseMessage>>? OnSend { get; set; } = onSend;

        public int CallCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return OnSend is null
                ? throw new InvalidOperationException("No fake response configured.")
                : OnSend(request);
        }
    }
}
