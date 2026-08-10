using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Fetches metadata from an explicitly allowlisted HTTP(S) source.
/// The response body is only drained to enforce its size bound; it is never
/// returned in a job result or written to the repository.
/// </summary>
public sealed class HttpChannelMediaCollector : IChannelMediaCollector
{
    private static readonly HttpStatusCode[] RedirectStatusCodes =
    [
        HttpStatusCode.Moved,
        HttpStatusCode.Redirect,
        HttpStatusCode.SeeOther,
        HttpStatusCode.TemporaryRedirect,
        HttpStatusCode.PermanentRedirect
    ];

    private readonly HttpClient _httpClient;
    private readonly MediaCollectorOptions _options;
    private readonly ILogger<HttpChannelMediaCollector> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<MediaJobResult>>> _inflight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MediaJobResult> _results = new(StringComparer.Ordinal);

    [ActivatorUtilitiesConstructor]
    public HttpChannelMediaCollector(
        HttpClient httpClient,
        IOptions<MediaCollectorOptions> options,
        ILogger<HttpChannelMediaCollector> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<HttpChannelMediaCollector>.Instance;
    }

    public HttpChannelMediaCollector(
        HttpClient httpClient,
        MediaCollectorOptions options,
        ILogger<HttpChannelMediaCollector>? logger = null)
        : this(
            httpClient,
            Microsoft.Extensions.Options.Options.Create(options ?? throw new ArgumentNullException(nameof(options))),
            logger ?? NullLogger<HttpChannelMediaCollector>.Instance)
    {
    }

    public async Task<MediaJobResult> CollectAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        var jobId = request?.JobId ?? string.Empty;
        if (cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(jobId);
        }

        if (request is null)
        {
            return MediaJobResult.Failed(jobId, "invalid_request", "Media job request is invalid.");
        }

        var validationFailure = request.Validate();
        if (validationFailure is not null)
        {
            return MediaJobResult.Failed(jobId, validationFailure, "Media job request is invalid.");
        }

        if (request.Kind != MediaJobKind.Collection)
        {
            return MediaJobResult.Failed(
                jobId,
                "unsupported_media_job",
                "Collector only accepts collection jobs.");
        }

        if (request.Inputs.Count != 1)
        {
            return MediaJobResult.Failed(
                jobId,
                "collection_input_count_invalid",
                "Collector accepts exactly one source asset.");
        }

        if (!TryValidateSource(request.Inputs[0].Uri, out var sourceUri, out var sourceFailure))
        {
            return MediaJobResult.Failed(jobId, sourceFailure!, "Source URI is not allowed.");
        }

        if (!_options.Enabled)
        {
            return MediaJobResult.Failed(
                jobId,
                MediaCollectionFailureCodes.Disabled,
                "Media collection provider is not configured.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return await CollectCoreAsync(request, sourceUri!, cancellationToken).ConfigureAwait(false);
        }

        if (_results.TryGetValue(request.IdempotencyKey, out var cachedResult))
        {
            return cachedResult;
        }

        var lazy = _inflight.GetOrAdd(
            request.IdempotencyKey,
            _ => new Lazy<Task<MediaJobResult>>(
                () => CollectCoreAsync(request, sourceUri!, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            var result = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (result.Status != MediaJobStatus.Cancelled)
            {
                _results.TryAdd(request.IdempotencyKey, result);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(jobId);
        }
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
            {
                _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<MediaJobResult>>>(
                    request.IdempotencyKey,
                    lazy));
            }
        }
    }

    private async Task<MediaJobResult> CollectCoreAsync(
        MediaJobRequest request,
        Uri sourceUri,
        CancellationToken callerCancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        timeout.CancelAfter(_options.EffectiveTimeout);

        var currentUri = sourceUri;
        var redirectCount = 0;

        try
        {
            while (true)
            {
                using var response = await SendAsync(currentUri, timeout.Token).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount >= _options.EffectiveMaxRedirects)
                    {
                        return MediaJobResult.Failed(
                            request.JobId,
                            MediaCollectionFailureCodes.RedirectLimitExceeded,
                            "Source redirect limit was exceeded.");
                    }

                    if (response.Headers.Location is null ||
                        !Uri.TryCreate(currentUri, response.Headers.Location, out var redirectedUri))
                    {
                        return MediaJobResult.Failed(
                            request.JobId,
                            MediaCollectionFailureCodes.InvalidRedirect,
                            "Source returned an invalid redirect.");
                    }

                    if (!TryValidateSource(redirectedUri.ToString(), out var validatedRedirect, out var redirectFailure))
                    {
                        return MediaJobResult.Failed(
                            request.JobId,
                            redirectFailure!,
                            "Redirect target is not allowed.");
                    }

                    currentUri = validatedRedirect!;
                    redirectCount++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var code = MediaCollectionFailureCodes.FromStatusCode(response.StatusCode);
                    _logger.LogWarning(
                        "Media source returned status {StatusCode}; mapped to {FailureCode}.",
                        (int)response.StatusCode,
                        code);
                    return MediaJobResult.Failed(request.JobId, code, "Media source rejected the request.");
                }

                if (!TryReadMediaType(response.Content.Headers.ContentType, out var mediaType) ||
                    !IsAllowedMediaType(mediaType!))
                {
                    return MediaJobResult.Failed(
                        request.JobId,
                        MediaCollectionFailureCodes.MediaTypeNotAllowed,
                        "Media source returned an unsupported media type.");
                }

                var maxBytes = _options.EffectiveMaxResponseBytes;
                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength is > 0 && declaredLength > maxBytes)
                {
                    return MediaJobResult.Failed(
                        request.JobId,
                        MediaCollectionFailureCodes.ResponseTooLarge,
                        "Media source response exceeds the configured size limit.");
                }

                var bytesRead = await DrainResponseAsync(response, maxBytes, timeout.Token).ConfigureAwait(false);
                if (bytesRead < 0)
                {
                    return MediaJobResult.Failed(
                        request.JobId,
                        MediaCollectionFailureCodes.ResponseTooLarge,
                        "Media source response exceeds the configured size limit.");
                }

                return new MediaJobResult(
                    request.JobId,
                    MediaJobStatus.Succeeded,
                    Assets:
                    [
                        new MediaAssetReference(
                            currentUri.ToString(),
                            mediaType!,
                            bytesRead)
                    ]);
            }
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            return MediaJobResult.Cancelled(request.JobId);
        }
        catch (OperationCanceledException)
        {
            return MediaJobResult.Failed(
                request.JobId,
                MediaCollectionFailureCodes.Timeout,
                "Media source request timed out.");
        }
        catch (HttpRequestException)
        {
            return MediaJobResult.Failed(
                request.JobId,
                MediaCollectionFailureCodes.Unreachable,
                "Media source could not be reached.");
        }
        catch (InvalidOperationException)
        {
            return MediaJobResult.Failed(
                request.JobId,
                MediaCollectionFailureCodes.InvalidResponse,
                "Media source returned an invalid response.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("video/*"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<long> DrainResponseAsync(
        HttpResponseMessage response,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
            if (total > maxBytes)
            {
                return -1;
            }
        }
    }

    private bool TryValidateSource(string rawUri, out Uri? uri, out string? failureCode)
    {
        var allowedHosts = new HashSet<string>(
            (_options.AllowedHosts ?? [])
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Select(host => host.Trim().TrimEnd('.').ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        if (!MediaSourceUriPolicy.TryValidate(rawUri, allowedHosts, out uri, out failureCode))
        {
            return false;
        }

        var allowedPorts = (_options.AllowedPorts ?? []).Where(port => port is > 0 and <= 65535).ToHashSet();
        if (!allowedPorts.Contains(uri!.Port))
        {
            uri = null;
            failureCode = MediaCollectionFailureCodes.PortNotAllowed;
            return false;
        }

        return true;
    }

    private bool IsAllowedMediaType(string mediaType) =>
        (_options.AllowedMediaTypes ?? []).Any(pattern =>
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            var normalized = pattern.Trim();
            return normalized.EndsWith("/*", StringComparison.Ordinal)
                ? mediaType.StartsWith(normalized[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(mediaType, normalized, StringComparison.OrdinalIgnoreCase);
        });

    private static bool TryReadMediaType(MediaTypeHeaderValue? contentType, out string? mediaType)
    {
        mediaType = contentType?.MediaType?.Trim();
        return !string.IsNullOrWhiteSpace(mediaType);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => RedirectStatusCodes.Contains(statusCode);
}
