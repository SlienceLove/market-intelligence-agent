using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed class HttpFrameOcrService : IFrameOcrService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly OcrHttpOptions _options;

    public HttpFrameOcrService(HttpClient httpClient, IOptions<OcrHttpOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<MediaJobResult> RecognizeAsync(
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
            return MediaJobResult.Failed(request.JobId, requestFailure, "OCR request is invalid.");
        }

        if (request.Kind != MediaJobKind.FrameOcr)
        {
            return MediaJobResult.Failed(request.JobId, "unsupported_media_job", "OCR service only accepts frame OCR jobs.");
        }

        if (!_options.Enabled)
        {
            return MediaJobResult.Failed(request.JobId, "provider_not_configured", "OCR provider is not configured.");
        }

        if (!TryGetEndpoint(_options.Endpoint, out var endpoint, out var endpointFailure))
        {
            return MediaJobResult.Failed(request.JobId, endpointFailure ?? "invalid_provider_endpoint", "OCR provider endpoint is invalid.");
        }

        var ocrOptions = _options.Ocr ?? new FrameOcrOptions();
        var inputFailure = FrameOcrInputPolicy.Validate(request.Inputs[0], ocrOptions);
        if (inputFailure is not null)
        {
            return MediaJobResult.Failed(request.JobId, inputFailure, "OCR input is not allowed.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.RequestTimeout > TimeSpan.Zero && _options.RequestTimeout != Timeout.InfiniteTimeSpan)
        {
            timeout.CancelAfter(_options.RequestTimeout);
        }

        var attempts = Math.Clamp(_options.MaxAttempts, 1, 5);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var response = await SendAsync(endpoint, request, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    var body = await ReadBodyAsync(response, timeout.Token, _options.MaxResponseBytes);
                    if (body.FailureCode is not null)
                    {
                        return MediaJobResult.Failed(request.JobId, body.FailureCode, body.FailureMessage!);
                    }

                    if (!TryParseFrames(body.Content!, out var frames))
                    {
                        return MediaJobResult.Failed(request.JobId, "invalid_ocr_response", "OCR provider returned an invalid response.");
                    }

                    var normalized = OcrResultNormalizer.Normalize(frames, ocrOptions);
                    return normalized.Count == 0
                        ? MediaJobResult.Failed(request.JobId, "empty_ocr_result", "OCR returned no usable frames.")
                        : new MediaJobResult(request.JobId, MediaJobStatus.Succeeded,
                            CorrelationId: request.CorrelationId,
                            IdempotencyKey: request.IdempotencyKey,
                            OcrFrames: normalized);
                }

                if (IsRetryable(response.StatusCode) && attempt < attempts)
                {
                    await DelayBeforeRetryAsync(attempt, timeout.Token, cancellationToken);
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
                return MediaJobResult.Failed(request.JobId, "ocr_timeout", "OCR provider request timed out.");
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                await DelayBeforeRetryAsync(attempt, timeout.Token, cancellationToken);
            }
            catch (HttpRequestException)
            {
                return MediaJobResult.Failed(request.JobId, "ocr_transport_error", "OCR provider could not be reached.");
            }
        }

        return MediaJobResult.Failed(request.JobId, "ocr_transport_error", "OCR provider could not be reached.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri endpoint, MediaJobRequest request, CancellationToken cancellationToken)
    {
        var input = request.Inputs[0];
        var payload = new OcrHttpRequest(
            request.JobId,
            request.CorrelationId,
            request.IdempotencyKey,
            new OcrHttpInput(input.Uri, input.MediaType, input.SizeBytes, input.Duration?.TotalSeconds),
            request.Parameters,
            _options.Ocr);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload, options: JsonOptions)
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.ApiKeyHeaderName))
        {
            message.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);
        }
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", request.IdempotencyKey);
        }
        return await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task DelayBeforeRetryAsync(int attempt, CancellationToken timeoutToken, CancellationToken callerToken)
    {
        var initial = _options.InitialRetryDelay;
        var maximum = _options.MaxRetryDelay;
        var delay = initial <= TimeSpan.Zero ? TimeSpan.Zero : initial * Math.Pow(2, Math.Max(0, attempt - 1));
        if (maximum > TimeSpan.Zero && delay > maximum) delay = maximum;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, timeoutToken);
        callerToken.ThrowIfCancellationRequested();
    }

    private static bool TryGetEndpoint(string? value, out Uri endpoint, out string? failure)
    {
        endpoint = default!;
        if (string.IsNullOrWhiteSpace(value)) { failure = "provider_not_configured"; return false; }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        { failure = "invalid_provider_endpoint"; return false; }
        endpoint = parsed; failure = null; return true;
    }

    private static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.RequestTimeout || status == (HttpStatusCode)425 || status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static MediaJobResult MapHttpFailure(string jobId, HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => MediaJobResult.Failed(jobId, "ocr_unauthorized", "OCR provider rejected the configured credentials."),
        HttpStatusCode.Forbidden => MediaJobResult.Failed(jobId, "ocr_forbidden", "OCR provider refused the request."),
        HttpStatusCode.TooManyRequests => MediaJobResult.Failed(jobId, "ocr_rate_limited", "OCR provider rate limited the request."),
        HttpStatusCode.RequestTimeout => MediaJobResult.Failed(jobId, "ocr_provider_timeout", "OCR provider timed out the request."),
        HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity => MediaJobResult.Failed(jobId, "ocr_invalid_request", "OCR provider rejected the request."),
        _ when (int)status >= 500 => MediaJobResult.Failed(jobId, "ocr_provider_unavailable", "OCR provider is temporarily unavailable."),
        _ => MediaJobResult.Failed(jobId, "ocr_provider_error", "OCR provider rejected the request.")
    };

    private static async Task<ReadBodyResult> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken, long maxBytes)
    {
        if (maxBytes <= 0 || response.Content.Headers.ContentLength > maxBytes)
            return new(null, "ocr_response_too_large", "OCR provider response exceeded the configured size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0L;
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) return new(null, "ocr_response_too_large", "OCR provider response exceeded the configured size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return new(Encoding.UTF8.GetString(buffer.ToArray()), null, null);
    }

    private static bool TryParseFrames(string json, out IReadOnlyList<OcrFrameText> frames)
    {
        frames = [];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array ? root : TryProperty(root, "frames", out var property) ? property : default;
            if (array.ValueKind != JsonValueKind.Array) return false;
            var parsed = new List<OcrFrameText>();
            foreach (var element in array.EnumerateArray())
            {
                if (!TryNumber(element, ["timestampSeconds", "timestamp"], out var timestamp) ||
                    !TryProperty(element, "text", out var text) || text.ValueKind != JsonValueKind.String ||
                    !double.IsFinite(timestamp) || timestamp < 0) return false;
                OcrBoundingBox? bounds = null;
                if (TryProperty(element, "bounds", out var boundsProperty) && boundsProperty.ValueKind != JsonValueKind.Null)
                {
                    if (!TryNumber(boundsProperty, "x", out var x) || !TryNumber(boundsProperty, "y", out var y) || !TryNumber(boundsProperty, "width", out var width) || !TryNumber(boundsProperty, "height", out var height)) return false;
                    bounds = new OcrBoundingBox(x, y, width, height);
                }
                double? confidence = null;
                if (TryProperty(element, "confidence", out var confidenceProperty) && confidenceProperty.ValueKind != JsonValueKind.Null)
                {
                    if (!TryNumber(confidenceProperty, out var value)) return false;
                    confidence = value;
                }
                var language = TryProperty(element, "language", out var languageProperty) && languageProperty.ValueKind == JsonValueKind.String ? languageProperty.GetString() : null;
                parsed.Add(new OcrFrameText(TimeSpan.FromSeconds(timestamp), text.GetString()!, bounds, language, confidence));
            }
            frames = parsed;
            return true;
        }
        catch (JsonException) { return false; }
        catch (OverflowException) { return false; }
    }

    private static bool TryNumber(JsonElement element, IReadOnlyList<string> names, out double value)
    {
        foreach (var name in names) if (TryProperty(element, name, out var property)) return TryNumber(property, out value);
        value = default; return false;
    }

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        if (TryProperty(element, name, out var property))
        {
            return TryNumber(property, out value);
        }

        value = default;
        return false;
    }

    private static bool TryNumber(JsonElement element, out double value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) return true;
        value = default; return false;
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default; return false;
    }

    private sealed record ReadBodyResult(string? Content, string? FailureCode, string? FailureMessage);
}

public sealed record OcrHttpRequest(string JobId, string? CorrelationId, string? IdempotencyKey, OcrHttpInput Input, IReadOnlyDictionary<string, string>? Parameters, FrameOcrOptions Options);

public sealed record OcrHttpInput(string Uri, string MediaType, long? SizeBytes, double? DurationSeconds);
