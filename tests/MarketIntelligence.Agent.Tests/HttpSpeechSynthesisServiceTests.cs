using System.Net;
using System.Text;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Media;
using MarketIntelligence.Agent.Infrastructure.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class HttpSpeechSynthesisServiceTests : IDisposable
{
    private readonly string _assetRoot = Path.Combine(Path.GetTempPath(), $"tts-tests-{Guid.NewGuid():N}");

    public HttpSpeechSynthesisServiceTests()
    {
        Directory.CreateDirectory(_assetRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_assetRoot))
        {
            Directory.Delete(_assetRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Disabled_provider_returns_not_configured_without_http_call()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var service = CreateService(handler, CreateOptions(enabled: false));

        var result = await service.SynthesizeAsync(ValidRequest());

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal("provider_not_configured", result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Successful_synthesis_keeps_segments_ordered_and_maps_audio_references()
    {
        var handler = new RecordingHandler(async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var segments = document.RootElement.GetProperty("segments").EnumerateArray().Reverse().Select(segment =>
                $"{{\"index\":{segment.GetProperty("index").GetInt32()},\"outputUri\":\"{segment.GetProperty("outputUri").GetString()}\",\"durationSeconds\":1.25,\"sampleRate\":16000,\"bytes\":32044,\"backend\":\"placeholder\"}}");
            return Response(HttpStatusCode.OK, $"{{\"segments\":[{string.Join(',', segments)}]}}");
        });
        var service = CreateService(handler, CreateOptions(maxSegmentLength: 5));

        var result = await service.SynthesizeAsync(ValidRequest("abcdefghij"));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        Assert.Collection(
            result.Assets!,
            first =>
            {
                Assert.Equal("asset://media/job-tts-http/audio-0000.wav", first.Uri);
                Assert.Equal(TimeSpan.FromSeconds(1.25), first.Duration);
                Assert.Equal(32044, first.SizeBytes);
            },
            second => Assert.Equal("asset://media/job-tts-http/audio-0001.wav", second.Uri));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Text_split_boundary_is_sent_to_provider_without_truncation()
    {
        string? exactBody = null;
        var exactHandler = new RecordingHandler(async request =>
        {
            exactBody = await request.Content!.ReadAsStringAsync();
            return ResponseForRequest(exactBody);
        });
        var exactService = CreateService(exactHandler, CreateOptions(maxSegmentLength: 8));

        var exact = await exactService.SynthesizeAsync(ValidRequest("12345678"));

        string? splitBody = null;
        var splitHandler = new RecordingHandler(async request =>
        {
            splitBody = await request.Content!.ReadAsStringAsync();
            return ResponseForRequest(splitBody);
        });
        var splitService = CreateService(splitHandler, CreateOptions(maxSegmentLength: 8));

        var split = await splitService.SynthesizeAsync(ValidRequest("123456789"));

        Assert.Equal(MediaJobStatus.Succeeded, exact.Status);
        Assert.Equal(MediaJobStatus.Succeeded, split.Status);
        Assert.Equal(1, SegmentCount(exactBody!));
        Assert.Equal(2, SegmentCount(splitBody!));
    }

    [Fact]
    public async Task Total_text_limit_fails_before_provider_call()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var service = CreateService(handler, CreateOptions(maxTextLength: 8));

        var result = await service.SynthesizeAsync(ValidRequest("123456789"));

        Assert.Equal("speech_text_too_long", result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Rate_limit_retries_are_bounded_and_classified()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.TooManyRequests, "{}")));
        var service = CreateService(handler, CreateOptions(maxRetries: 2));

        var result = await service.SynthesizeAsync(ValidRequest());

        Assert.Equal("rate_limited", result.FailureCode);
        Assert.Equal(MediaFailureCategory.RateLimited, result.ErrorCategory);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Transient_provider_failure_retries_and_returns_provider_unavailable()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable, "{}")));
        var service = CreateService(handler, CreateOptions(maxRetries: 1));

        var result = await service.SynthesizeAsync(ValidRequest());

        Assert.Equal("provider_unavailable", result.FailureCode);
        Assert.Equal(MediaFailureCategory.ProviderUnavailable, result.ErrorCategory);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Permanent_4xx_failure_does_not_retry()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.BadRequest, "{}")));
        var service = CreateService(handler, CreateOptions(maxRetries: 3));

        var result = await service.SynthesizeAsync(ValidRequest());

        Assert.Equal("invalid_request", result.FailureCode);
        Assert.Equal(MediaFailureCategory.Validation, result.ErrorCategory);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Timeout_and_cancellation_are_classified_separately()
    {
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("provider timeout"));
        var service = CreateService(handler, CreateOptions(maxRetries: 0));

        var timeout = await service.SynthesizeAsync(ValidRequest());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await service.SynthesizeAsync(ValidRequest(jobId: "cancelled-tts"), cancellation.Token);

        Assert.Equal("timeout", timeout.FailureCode);
        Assert.Equal(MediaFailureCategory.Timeout, timeout.ErrorCategory);
        Assert.Equal(MediaJobStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public async Task Unsafe_output_reference_from_resolver_fails_before_provider_call()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(Response(HttpStatusCode.OK, "{}")));
        var service = new HttpSpeechSynthesisService(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(CreateOptions()),
            new FailingPathResolver("unsafe_asset_reference"));

        var result = await service.SynthesizeAsync(ValidRequest());

        Assert.Equal("unsafe_asset_reference", result.FailureCode);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Logs_do_not_include_narration_text_or_service_key()
    {
        const string secretText = "do-not-log-full-narration";
        const string serviceKey = "do-not-log-service-key";
        var logger = new RecordingLogger<HttpSpeechSynthesisService>();
        var handler = new RecordingHandler(async request => ResponseForRequest(await request.Content!.ReadAsStringAsync()));
        var service = CreateService(handler, CreateOptions(serviceKey: serviceKey), logger);

        var result = await service.SynthesizeAsync(ValidRequest(secretText));

        Assert.Equal(MediaJobStatus.Succeeded, result.Status);
        var rendered = string.Join('\n', logger.Messages);
        Assert.DoesNotContain(secretText, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(serviceKey, rendered, StringComparison.Ordinal);
        Assert.Contains("placeholder", rendered, StringComparison.Ordinal);
    }

    private HttpSpeechSynthesisService CreateService(
        RecordingHandler handler,
        TtsHttpOptions options,
        ILogger<HttpSpeechSynthesisService>? logger = null) =>
        new(
            new HttpClient(handler),
            Microsoft.Extensions.Options.Options.Create(options),
            new MediaAssetPathResolver(Microsoft.Extensions.Options.Options.Create(new MediaOptions { AssetRoot = _assetRoot })),
            logger);

    private static TtsHttpOptions CreateOptions(
        bool enabled = true,
        int maxRetries = 0,
        int maxTextLength = 10_000,
        int maxSegmentLength = 800,
        string serviceKey = "test-secret") => new()
        {
            Enabled = enabled,
            Endpoint = "https://provider.example/tts",
            ServiceKey = serviceKey,
            TimeoutSeconds = 5,
            MaxRetries = maxRetries,
            MaxTextLength = maxTextLength,
            MaxSegmentLength = maxSegmentLength,
            MaxTotalDurationSeconds = 600,
            OutputFormat = "wav",
            SampleRate = 16_000,
            InitialRetryDelay = TimeSpan.Zero,
            MaxRetryDelay = TimeSpan.Zero
        };

    private static MediaJobRequest ValidRequest(string text = "hello", string jobId = "job-tts-http") => new(
        jobId,
        MediaJobKind.SpeechSynthesis,
        [new MediaAssetReference("asset://fixture/script", "text/plain", 1)],
        Parameters: new Dictionary<string, string>
        {
            ["text"] = text,
            ["voice"] = "default",
            ["language"] = "zh-CN"
        });

    private static int SegmentCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("segments").GetArrayLength();
    }

    private static HttpResponseMessage ResponseForRequest(string requestBody)
    {
        using var document = JsonDocument.Parse(requestBody);
        var segments = document.RootElement.GetProperty("segments").EnumerateArray().Select(segment =>
            $"{{\"index\":{segment.GetProperty("index").GetInt32()},\"outputUri\":\"{segment.GetProperty("outputUri").GetString()}\",\"durationSeconds\":1,\"sampleRate\":16000,\"bytes\":32044,\"backend\":\"placeholder\"}}");
        return Response(HttpStatusCode.OK, $"{{\"segments\":[{string.Join(',', segments)}]}}");
    }

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) => new(statusCode)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> onSend) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return onSend(request);
        }
    }

    private sealed class FailingPathResolver(string failureCode) : IMediaAssetPathResolver
    {
        public bool IsConfigured => true;
        public MediaPathResolution ResolveInput(string uri) => MediaPathResolution.Fail(failureCode);
        public MediaPathResolution ResolveOutput(string relativePath) => MediaPathResolution.Fail(failureCode);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
