using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed class HttpSpeechSynthesisService : ISpeechSynthesisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly TtsHttpOptions _options;
    private readonly IMediaAssetPathResolver _pathResolver;
    private readonly ILogger<HttpSpeechSynthesisService> _logger;

    public HttpSpeechSynthesisService(
        HttpClient httpClient,
        IOptions<TtsHttpOptions> options,
        IMediaAssetPathResolver pathResolver,
        ILogger<HttpSpeechSynthesisService>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _logger = logger ?? NullLogger<HttpSpeechSynthesisService>.Instance;
    }

    public async Task<MediaJobResult> SynthesizeAsync(MediaJobRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(request.JobId);
        }

        if (!TryGetConfiguredEndpoint(out var endpoint))
        {
            return MediaJobResult.Failed(request.JobId, "provider_not_configured", "TTS provider is not configured.");
        }

        var requestFailure = request.Validate();
        if (requestFailure is not null)
        {
            return MediaJobResult.Failed(request.JobId, requestFailure, "Speech request is invalid.");
        }

        if (request.Kind != MediaJobKind.SpeechSynthesis)
        {
            return MediaJobResult.Failed(request.JobId, "unsupported_media_job", "TTS service only accepts speech synthesis jobs.");
        }

        var inputFailure = SpeechSynthesisInputPolicy.Validate(
            request,
            CreateSpeechOptions(),
            out var text,
            out var voice,
            out var language);
        if (inputFailure is not null)
        {
            return MediaJobResult.Failed(request.JobId, inputFailure, "Speech input is not allowed.");
        }

        var chunks = SpeechTextChunker.Split(text, _options.MaxSegmentLength);
        var plannedOutputs = BuildOutputs(request.JobId, chunks);
        if (plannedOutputs.FailureCode is not null)
        {
            return MediaJobResult.Failed(request.JobId, plannedOutputs.FailureCode, "TTS output asset is not allowed.");
        }

        var attempts = Math.Clamp(_options.MaxRetries, 0, 4) + 1;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 1, 300)));

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await SendAsync(endpoint, request, voice, language, plannedOutputs.Items!, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var body = await ReadBodyAsync(response, timeout.Token, _options.MaxResponseBytes);
                    if (body.FailureCode is not null)
                    {
                        return Failure(request, body.FailureCode, body.FailureMessage!, MapCategory(body.FailureCode));
                    }

                    if (!TryMapResponse(body.Content!, plannedOutputs.Items!, out var assets, out var totalDuration, out var backend))
                    {
                        return Failure(request, "invalid_provider_response", "TTS provider returned an invalid response.", MediaFailureCategory.Validation);
                    }

                    _logger.LogInformation(
                        "TTS synthesis completed job_id={JobId} language={Language} segments={SegmentCount} total_duration_seconds={TotalDurationSeconds} backend={Backend} status={Status}",
                        request.JobId,
                        language,
                        assets.Count,
                        totalDuration.TotalSeconds,
                        backend,
                        "succeeded");

                    return new MediaJobResult(request.JobId, MediaJobStatus.Succeeded, Assets: assets);
                }

                if (IsRetryable(response.StatusCode) && attempt < attempts)
                {
                    await DelayBeforeRetryAsync(response, attempt, timeout.Token, cancellationToken);
                    continue;
                }

                return MapHttpFailure(request, response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return MediaJobResult.Cancelled(request);
            }
            catch (OperationCanceledException)
            {
                return Failure(request, "timeout", "TTS provider request timed out.", MediaFailureCategory.Timeout);
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await DelayBeforeRetryAsync(null, attempt, timeout.Token, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return Failure(request, "provider_unavailable", "TTS provider could not be reached.", MediaFailureCategory.ProviderUnavailable);
            }
        }

        return Failure(request, "provider_unavailable", "TTS provider could not be reached.", MediaFailureCategory.ProviderUnavailable);
    }

    private bool TryGetConfiguredEndpoint(out Uri endpoint)
    {
        endpoint = default!;
        if (!_options.Enabled ||
            string.IsNullOrWhiteSpace(_options.Endpoint) ||
            string.IsNullOrWhiteSpace(_options.ServiceKey) ||
            string.IsNullOrWhiteSpace(_options.ServiceKeyHeaderName) ||
            !Uri.TryCreate(_options.Endpoint, UriKind.Absolute, out var parsedEndpoint) ||
            (parsedEndpoint.Scheme != Uri.UriSchemeHttp && parsedEndpoint.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        endpoint = parsedEndpoint;
        return true;
    }

    private SpeechSynthesisOptions CreateSpeechOptions() => new()
    {
        MaxTextCharacters = _options.MaxTextLength,
        MaxChunkCharacters = _options.MaxSegmentLength,
        MaxDuration = TimeSpan.FromSeconds(_options.MaxTotalDurationSeconds)
    };

    private OutputBuildResult BuildOutputs(string jobId, IReadOnlyList<string> chunks)
    {
        var outputs = new List<PlannedOutput>(chunks.Count);
        for (var index = 0; index < chunks.Count; index++)
        {
            var relativePath = $"media/{jobId}/audio-{index:D4}.wav";
            var resolution = _pathResolver.ResolveOutput(relativePath);
            if (!resolution.Succeeded)
            {
                return new OutputBuildResult(null, resolution.FailureCode ?? "invalid_output_asset");
            }

            outputs.Add(new PlannedOutput(
                index,
                chunks[index],
                $"temp://media/{relativePath}",
                $"asset://{relativePath}"));
        }

        return new OutputBuildResult(outputs, null);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        MediaJobRequest request,
        string voice,
        string language,
        IReadOnlyList<PlannedOutput> outputs,
        CancellationToken cancellationToken)
    {
        var payload = new TtsHttpRequest(
            request.JobId,
            request.CorrelationId,
            request.IdempotencyKey,
            voice,
            language,
            "wav",
            _options.SampleRate,
            outputs.Select(output => new TtsHttpSegment(output.Index, output.Text, output.TempUri)).ToArray());

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        message.Headers.TryAddWithoutValidation(_options.ServiceKeyHeaderName!, _options.ServiceKey);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }

        return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task DelayBeforeRetryAsync(HttpResponseMessage? response, int attempt, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        var retryAfter = response?.Headers.RetryAfter?.Delta;
        var exponential = TimeSpan.FromMilliseconds(_options.InitialRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
        var delay = retryAfter ?? (exponential > _options.MaxRetryDelay ? _options.MaxRetryDelay : exponential);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, timeoutToken);
        callerToken.ThrowIfCancellationRequested();
    }

    private bool TryMapResponse(
        string json,
        IReadOnlyList<PlannedOutput> expected,
        out IReadOnlyList<MediaAssetReference> assets,
        out TimeSpan totalDuration,
        out string backend)
    {
        assets = [];
        totalDuration = TimeSpan.Zero;
        backend = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetProperty(document.RootElement, "segments", out var segments) || segments.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new Dictionary<int, (long Bytes, TimeSpan Duration, string Backend)>();
            foreach (var segment in segments.EnumerateArray())
            {
                if (!TryGetInt32(segment, "index", out var index) ||
                    !TryGetProperty(segment, "outputUri", out var outputUri) || outputUri.ValueKind != JsonValueKind.String ||
                    !TryGetInt64(segment, "bytes", out var bytes) || bytes < 0 ||
                    !TryGetDouble(segment, "durationSeconds", out var durationSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0 ||
                    !TryGetInt32(segment, "sampleRate", out var sampleRate) || sampleRate != _options.SampleRate ||
                    !TryGetProperty(segment, "backend", out var backendProperty) || backendProperty.ValueKind != JsonValueKind.String ||
                    !IsSafeToken(backendProperty.GetString()) ||
                    index < 0 || index >= expected.Count ||
                    !string.Equals(outputUri.GetString(), expected[index].TempUri, StringComparison.Ordinal) ||
                    !parsed.TryAdd(index, (bytes, TimeSpan.FromSeconds(durationSeconds), backendProperty.GetString()!)))
                {
                    return false;
                }
            }

            if (parsed.Count != expected.Count)
            {
                return false;
            }

            var ordered = new List<MediaAssetReference>(expected.Count);
            foreach (var expectedOutput in expected)
            {
                var parsedOutput = parsed[expectedOutput.Index];
                totalDuration += parsedOutput.Duration;
                if (totalDuration > TimeSpan.FromSeconds(_options.MaxTotalDurationSeconds))
                {
                    return false;
                }

                backend = string.IsNullOrEmpty(backend) ? parsedOutput.Backend : backend;
                if (!string.Equals(backend, parsedOutput.Backend, StringComparison.Ordinal))
                {
                    return false;
                }

                ordered.Add(new MediaAssetReference(expectedOutput.AssetUri, "audio/wav", parsedOutput.Bytes, parsedOutput.Duration));
            }

            assets = ordered;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool IsSafeToken(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == (HttpStatusCode)425 ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static MediaJobResult MapHttpFailure(MediaJobRequest request, HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => Failure(request, "unauthorized", "TTS provider rejected the configured credentials.", MediaFailureCategory.Authorization),
        HttpStatusCode.Forbidden => Failure(request, "forbidden", "TTS provider refused the request.", MediaFailureCategory.Authorization),
        HttpStatusCode.RequestTimeout => Failure(request, "timeout", "TTS provider timed out the request.", MediaFailureCategory.Timeout),
        HttpStatusCode.TooManyRequests => Failure(request, "rate_limited", "TTS provider rate limited the request.", MediaFailureCategory.RateLimited),
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => Failure(request, "invalid_request", "TTS provider rejected the synthesis request.", MediaFailureCategory.Validation),
        HttpStatusCode.NotFound => Failure(request, "provider_unavailable", "TTS provider endpoint was not found.", MediaFailureCategory.ProviderUnavailable),
        _ when (int)statusCode >= 500 => Failure(request, "provider_unavailable", "TTS provider is temporarily unavailable.", MediaFailureCategory.ProviderUnavailable),
        _ => Failure(request, "synthesis_failed", "TTS provider rejected the synthesis request.", MediaFailureCategory.Unknown)
    };

    private static async Task<ReadBodyResult> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken, long maxBytes)
    {
        if (maxBytes <= 0 || response.Content.Headers.ContentLength > maxBytes)
        {
            return new(null, "tts_response_too_large", "TTS provider response exceeded the configured size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                return new(null, "tts_response_too_large", "TTS provider response exceeded the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return new(Encoding.UTF8.GetString(buffer.ToArray()), null, null);
    }

    private static MediaJobResult Failure(MediaJobRequest request, string code, string message, MediaFailureCategory category) =>
        new(request.JobId, MediaJobStatus.Failed, code, message, CorrelationId: request.CorrelationId, IdempotencyKey: request.IdempotencyKey, FailureCategory: category);

    private static MediaFailureCategory MapCategory(string? code) => code?.ToLowerInvariant() switch
    {
        "invalid_request" => MediaFailureCategory.Validation,
        "unauthorized" or "forbidden" => MediaFailureCategory.Authorization,
        "rate_limited" => MediaFailureCategory.RateLimited,
        "timeout" => MediaFailureCategory.Timeout,
        "provider_unavailable" => MediaFailureCategory.ProviderUnavailable,
        "backend_not_configured" => MediaFailureCategory.ProviderUnavailable,
        "tts_response_too_large" => MediaFailureCategory.LimitExceeded,
        _ => MediaFailureCategory.Unknown
    };

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        if (TryGetProperty(element, name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        if (TryGetProperty(element, name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt64(out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        if (TryGetProperty(element, name, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private sealed record PlannedOutput(int Index, string Text, string TempUri, string AssetUri);

    private sealed record OutputBuildResult(IReadOnlyList<PlannedOutput>? Items, string? FailureCode);

    private sealed record ReadBodyResult(string? Content, string? FailureCode, string? FailureMessage);
}

public sealed record TtsHttpRequest(
    string JobId,
    string? CorrelationId,
    string? IdempotencyKey,
    string Voice,
    string Language,
    string OutputFormat,
    int SampleRate,
    IReadOnlyList<TtsHttpSegment> Segments);

public sealed record TtsHttpSegment(int Index, string Text, string OutputUri);
