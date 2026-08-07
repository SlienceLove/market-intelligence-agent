using System.Collections.Concurrent;

namespace MarketIntelligence.Agent.Application.Media;

public sealed class FakeChannelMediaCollector(IReadOnlySet<string> allowedHosts) : IChannelMediaCollector
{
    private readonly ConcurrentDictionary<string, MediaJobResult> _results = new(StringComparer.Ordinal);

    public Task<MediaJobResult> CollectAsync(MediaJobRequest request, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(MediaJobResult.Cancelled(request.JobId));
        }

        var validationFailure = request.Validate();
        if (validationFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request.JobId, validationFailure, "Media job request is invalid."));
        }

        if (request.Kind != MediaJobKind.Collection)
        {
            return Task.FromResult(MediaJobResult.Failed(
                request.JobId,
                "unsupported_media_job",
                "Collector only accepts collection jobs."));
        }

        if (!MediaSourceUriPolicy.TryValidate(request.Inputs[0].Uri, allowedHosts, out _, out var sourceFailure))
        {
            return Task.FromResult(MediaJobResult.Failed(
                request.JobId,
                sourceFailure!,
                "Source URI is not allowed."));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Task.FromResult(CreateSuccess(request.JobId));
        }

        var result = _results.GetOrAdd(request.IdempotencyKey, _ => CreateSuccess(request.JobId));
        return Task.FromResult(result);
    }

    private static MediaJobResult CreateSuccess(string jobId) =>
        new(
            jobId,
            MediaJobStatus.Succeeded,
            Assets: [new MediaAssetReference($"asset://fixture/{jobId}", "video/mp4", 1024, TimeSpan.FromSeconds(10))]);
}
