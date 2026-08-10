using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Sends a canonical transcription request to a configured HTTP provider.
/// Provider-specific credentials and model names stay in configuration; the
/// application only consumes the stable <see cref="ITranscriptionService"/> contract.
/// </summary>
public sealed class HttpTranscriptionService : ITranscriptionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AsrHttpOptions _options;

    public HttpTranscriptionService(HttpClient httpClient, IOptions<AsrHttpOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<MediaJobResult> TranscribeAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(request.JobId);
        }

        var requestFailure = request.Validate();
        if (requestFailure is not null)
        {
            return MediaJobResult.Failed(
                request.JobId,
                requestFailure,
                "Transcription request is invalid.");
        }

        if (request.Kind != MediaJobKind.Transcription)
        {
            return MediaJobResult.Failed(
                request.JobId,
                "unsupported_media_job",
                "Transcription service only accepts transcription jobs.");
        }

        if (!_options.Enabled)
        {
            return MediaJobResult.Failed(
                request.JobId,
                "provider_not_configured",
                "ASR provider is not configured.");
        }

        if (!TryGetEndpoint(_options.Endpoint, out var endpoint, out var endpointFailure))
        {
            return MediaJobResult.Failed(
                request.JobId,
                endpointFailure,
                endpointFailure == "provider_not_configured"
                    ? "ASR provider is not configured."
                    : "ASR provider endpoint is invalid.");
        }

        var inputFailure = TranscriptionInputPolicy.Validate(
            request.Inputs[0],
            _options.Transcription ?? new TranscriptionOptions());
        if (inputFailure is not null)
        {
            return MediaJobResult.Failed(
                request.JobId,
                inputFailure,
                "Audio input is not allowed.");
        }

        var attempts = Math.Clamp(_options.MaxAttempts, 1, 5);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout > TimeSpan.Zero &&
            _options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_options.RequestTimeout);
        }

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await SendAsync(
                    endpoint,
                    request,
                    timeout.Token,
                    cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var body = await ReadBodyAsync(response, timeout.Token, _options.MaxResponseBytes);
                    if (body.FailureCode is not null)
                    {
                        return MediaJobResult.Failed(
                            request.JobId,
                            body.FailureCode,
                            body.FailureMessage!);
                    }

                    if (!TryParseSegments(body.Content!, out var segments))
                    {
                        return MediaJobResult.Failed(
                            request.JobId,
                            "invalid_provider_response",
                            "ASR provider returned an invalid response.");
                    }

                    return TimedTextNormalizer.Normalize(
                        request.JobId,
                        segments,
                        _options.Transcription ?? new TranscriptionOptions());
                }

                if (IsRetryable(response.StatusCode) && attempt < attempts)
                {
                    await DelayBeforeRetryAsync(response, attempt, timeout.Token, cancellationToken);
                    continue;
                }

                return MapHttpFailure(request.JobId, response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return MediaJobResult.Cancelled(request.JobId);
            }
            catch (OperationCanceledException)
            {
                return MediaJobResult.Failed(
                    request.JobId,
                    "asr_timeout",
                    "ASR provider request timed out.");
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await DelayBeforeRetryAsync(null, attempt, timeout.Token, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return MediaJobResult.Failed(
                    request.JobId,
                    "asr_transport_error",
                    "ASR provider could not be reached.");
            }
        }

        return MediaJobResult.Failed(
            request.JobId,
            "asr_transport_error",
            "ASR provider could not be reached.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri endpoint,
        MediaJobRequest request,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        var input = request.Inputs[0];
        var payload = new AsrHttpRequest(
            request.JobId,
            request.CorrelationId,
            request.IdempotencyKey,
            new AsrHttpInput(
                input.Uri,
                input.MediaType,
                input.SizeBytes,
                input.Duration?.TotalSeconds),
            request.Parameters,
            _options.Model);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey) &&
            !string.IsNullOrWhiteSpace(_options.ApiKeyHeaderName))
        {
            message.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }

        return await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutToken);
    }

    private async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        int attempt,
        CancellationToken timeoutToken,
        CancellationToken callerToken)
    {
        var delay = GetRetryAfter(response) ?? GetExponentialDelay(attempt);
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        await Task.Delay(delay, timeoutToken);
        callerToken.ThrowIfCancellationRequested();
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage? response)
    {
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        var delay = retryAfter.Delta;
        if (delay is null && retryAfter.Date is { } date)
        {
            delay = date - DateTimeOffset.UtcNow;
        }

        if (delay is null)
        {
            return null;
        }

        return ClampDelay(delay.Value);
    }

    private TimeSpan GetExponentialDelay(int attempt)
    {
        var initial = _options.InitialRetryDelay;
        if (initial <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var ticks = initial.Ticks * multiplier;
        if (ticks >= TimeSpan.MaxValue.Ticks)
        {
            return ClampDelay(TimeSpan.MaxValue);
        }

        return ClampDelay(TimeSpan.FromTicks((long)ticks));
    }

    private TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var maximum = _options.MaxRetryDelay;
        return maximum > TimeSpan.Zero && delay > maximum ? maximum : delay;
    }

    private static bool TryGetEndpoint(
        string? configuredEndpoint,
        out Uri endpoint,
        out string failureCode)
    {
        endpoint = default!;
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            failureCode = "provider_not_configured";
            return false;
        }

        if (!Uri.TryCreate(configuredEndpoint, UriKind.Absolute, out var parsedEndpoint) ||
            (parsedEndpoint.Scheme != Uri.UriSchemeHttp && parsedEndpoint.Scheme != Uri.UriSchemeHttps))
        {
            failureCode = "invalid_provider_endpoint";
            return false;
        }

        endpoint = parsedEndpoint;
        failureCode = string.Empty;
        return true;
    }

    private static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == (HttpStatusCode)425 ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static MediaJobResult MapHttpFailure(string jobId, HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.Unauthorized => MediaJobResult.Failed(
                jobId,
                "asr_unauthorized",
                "ASR provider rejected the configured credentials."),
            HttpStatusCode.Forbidden => MediaJobResult.Failed(
                jobId,
                "asr_forbidden",
                "ASR provider refused the request."),
            HttpStatusCode.RequestTimeout => MediaJobResult.Failed(
                jobId,
                "asr_provider_timeout",
                "ASR provider timed out the request."),
            HttpStatusCode.TooManyRequests => MediaJobResult.Failed(
                jobId,
                "asr_rate_limited",
                "ASR provider rate limited the request."),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => MediaJobResult.Failed(
                jobId,
                "asr_invalid_request",
                "ASR provider rejected the transcription request."),
            HttpStatusCode.NotFound => MediaJobResult.Failed(
                jobId,
                "asr_endpoint_not_found",
                "ASR provider endpoint was not found."),
            _ when (int)statusCode >= 500 => MediaJobResult.Failed(
                jobId,
                "asr_provider_unavailable",
                "ASR provider is temporarily unavailable."),
            _ => MediaJobResult.Failed(
                jobId,
                "asr_provider_error",
                "ASR provider rejected the transcription request.")
        };

    private static async Task<ReadBodyResult> ReadBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken,
        long maxBytes)
    {
        if (maxBytes <= 0)
        {
            return new ReadBodyResult(
                null,
                "asr_response_too_large",
                "ASR provider response exceeded the configured size limit.");
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            return new ReadBodyResult(
                null,
                "asr_response_too_large",
                "ASR provider response exceeded the configured size limit.");
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
                return new ReadBodyResult(
                    null,
                    "asr_response_too_large",
                    "ASR provider response exceeded the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        return new ReadBodyResult(Encoding.UTF8.GetString(buffer.ToArray()), null, null);
    }

    private static bool TryParseSegments(
        string json,
        out IReadOnlyList<TimedTextSegment> segments)
    {
        segments = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : TryGetProperty(root, "segments", out var segmentProperty)
                    ? segmentProperty
                    : TryGetProperty(root, "timedText", out var timedTextProperty)
                        ? timedTextProperty
                        : default;

            if (array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<TimedTextSegment>();
            foreach (var element in array.EnumerateArray())
            {
                if (!TryReadNumber(element, ["startSeconds", "start"], out var start) ||
                    !TryReadNumber(element, ["endSeconds", "end"], out var end) ||
                    !TryGetProperty(element, "text", out var textProperty) ||
                    textProperty.ValueKind != JsonValueKind.String ||
                    !double.IsFinite(start) ||
                    !double.IsFinite(end) ||
                    start > TimeSpan.MaxValue.TotalSeconds ||
                    start < TimeSpan.MinValue.TotalSeconds ||
                    end > TimeSpan.MaxValue.TotalSeconds ||
                    end < TimeSpan.MinValue.TotalSeconds)
                {
                    return false;
                }

                double? confidence = null;
                if (TryGetProperty(element, "confidence", out var confidenceProperty) &&
                    confidenceProperty.ValueKind != JsonValueKind.Null)
                {
                    if (!TryReadNumber(confidenceProperty, out var value) || !double.IsFinite(value))
                    {
                        return false;
                    }

                    confidence = value;
                }

                parsed.Add(new TimedTextSegment(
                    TimeSpan.FromSeconds(start),
                    TimeSpan.FromSeconds(end),
                    textProperty.GetString()!,
                    confidence));
            }

            segments = parsed;
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

    private static bool TryReadNumber(
        JsonElement element,
        IReadOnlyList<string> names,
        out double value)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(element, name, out var property))
            {
                return TryReadNumber(property, out value);
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadNumber(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        out JsonElement value)
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

    private sealed record ReadBodyResult(
        string? Content,
        string? FailureCode,
        string? FailureMessage);
}

public sealed record AsrHttpRequest(
    string JobId,
    string? CorrelationId,
    string? IdempotencyKey,
    AsrHttpInput Input,
    IReadOnlyDictionary<string, string>? Parameters,
    string? Model);

public sealed record AsrHttpInput(
    string Uri,
    string MediaType,
    long? SizeBytes,
    double? DurationSeconds);
