using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketIntelligence.Agent.Application.Media;

public interface IMediaJobCoordinator
{
    Task<MediaJobResult> SubmitAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default);

    MediaJobResult? Get(string jobId);

    bool Cancel(string jobId);
}

/// <summary>
/// Development-safe job boundary. The queue is deliberately in-memory and bounded;
/// production deployments must replace it with a durable queue and status store.
/// </summary>
public sealed class InMemoryMediaJobCoordinator(
    IChannelMediaCollector collector,
    ITranscriptionService transcription,
    IFrameOcrService ocr,
    ISpeechSynthesisService speech,
    IVideoCompositionService composition,
    ILogger<InMemoryMediaJobCoordinator> logger) : BackgroundService, IMediaJobCoordinator
{
    private readonly Channel<JobEntry> _queue = Channel.CreateBounded<JobEntry>(
        new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly object _submissionGate = new();

    public Task<MediaJobResult> SubmitAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(MediaJobResult.Cancelled(request?.JobId ?? string.Empty));
        }

        if (request is null)
        {
            return Task.FromResult(MediaJobResult.Failed(string.Empty, "invalid_request", "Media job request is invalid."));
        }

        var validationFailure = request.Validate();
        if (validationFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request, validationFailure, "Media job request is invalid."));
        }

        lock (_submissionGate)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) &&
                _idempotency.TryGetValue(request.IdempotencyKey, out var existingJobId) &&
                _jobs.TryGetValue(existingJobId, out var existing))
            {
                return Task.FromResult(existing.Snapshot());
            }

            if (_jobs.ContainsKey(request.JobId))
            {
                return Task.FromResult(MediaJobResult.Failed(
                    request,
                    "job_conflict",
                    "A job with the requested identifier already exists."));
            }

            var entry = new JobEntry(request);
            if (!_jobs.TryAdd(request.JobId, entry))
            {
                return Task.FromResult(MediaJobResult.Failed(
                    request,
                    "job_conflict",
                    "A job with the requested identifier already exists."));
            }

            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            {
                _idempotency[request.IdempotencyKey] = request.JobId;
            }

            if (!_queue.Writer.TryWrite(entry))
            {
                entry.TrySet(MediaJobResult.Failed(
                    request,
                    "queue_unavailable",
                    "The media job queue is temporarily unavailable."));
                return Task.FromResult(entry.Snapshot());
            }

            // The HTTP contract is asynchronous: the caller receives acceptance even
            // if the worker completes the job before this method returns.
            return Task.FromResult(request.Accepted());
        }
    }

    public MediaJobResult? Get(string jobId) =>
        string.IsNullOrWhiteSpace(jobId) || !_jobs.TryGetValue(jobId, out var entry)
            ? null
            : entry.Snapshot();

    public bool Cancel(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            return false;
        }

        entry.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var entry in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (entry.IsTerminal)
            {
                continue;
            }

            entry.TrySet(entry.Request.Running());
            try
            {
                var result = await ExecuteJobAsync(entry.Request, entry.Cancellation.Token);
                if (entry.Cancellation.IsCancellationRequested)
                {
                    entry.TrySet(MediaJobResult.Cancelled(entry.Request));
                }
                else
                {
                    entry.TrySet(result.WithContext(entry.Request));
                }
            }
            catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
            {
                entry.TrySet(MediaJobResult.Cancelled(entry.Request));
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Media job {JobId} failed inside the coordinator.",
                    entry.Request.JobId);
                entry.TrySet(MediaJobResult.Failed(
                    entry.Request,
                    "internal_error",
                    "The media job failed inside the service."));
            }
        }
    }

    private Task<MediaJobResult> ExecuteJobAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken) => request.Kind switch
        {
            MediaJobKind.Collection => collector.CollectAsync(request, cancellationToken),
            MediaJobKind.Transcription => transcription.TranscribeAsync(request, cancellationToken),
            MediaJobKind.FrameOcr => ocr.RecognizeAsync(request, cancellationToken),
            MediaJobKind.SpeechSynthesis => speech.SynthesizeAsync(request, cancellationToken),
            MediaJobKind.VideoComposition => composition.ComposeAsync(request, cancellationToken),
            _ => Task.FromResult(MediaJobResult.Failed(
                request,
                "unsupported_media_job",
                "The requested media operation is not supported."))
        };

    private sealed class JobEntry
    {
        private readonly object _gate = new();
        private MediaJobResult _result;

        public JobEntry(MediaJobRequest request)
        {
            Request = request;
            _result = request.Accepted();
            Cancellation = new CancellationTokenSource();
        }

        public MediaJobRequest Request { get; }

        public CancellationTokenSource Cancellation { get; }

        public bool IsTerminal
        {
            get
            {
                lock (_gate)
                {
                    return _result.IsTerminal;
                }
            }
        }

        public MediaJobResult Snapshot()
        {
            lock (_gate)
            {
                return _result;
            }
        }

        public bool TrySet(MediaJobResult next)
        {
            lock (_gate)
            {
                if (!_result.Status.CanTransitionTo(next.Status))
                {
                    return false;
                }

                _result = next;
                return true;
            }
        }

        public void Cancel()
        {
            Cancellation.Cancel();
            TrySet(MediaJobResult.Cancelled(Request));
        }
    }
}
