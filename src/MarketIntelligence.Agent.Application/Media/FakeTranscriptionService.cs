namespace MarketIntelligence.Agent.Application.Media;

public sealed class FakeTranscriptionService(
    IReadOnlyList<TimedTextSegment> fixture,
    TranscriptionOptions? options = null) : ITranscriptionService
{
    private readonly TranscriptionOptions _options = options ?? new();

    public Task<MediaJobResult> TranscribeAsync(
        MediaJobRequest request,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(MediaJobResult.Cancelled(request.JobId));
        }

        var requestFailure = request.Validate();
        if (requestFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request.JobId, requestFailure, "Transcription request is invalid."));
        }

        if (request.Kind != MediaJobKind.Transcription)
        {
            return Task.FromResult(MediaJobResult.Failed(
                request.JobId,
                "unsupported_media_job",
                "Transcription service only accepts transcription jobs."));
        }

        var inputFailure = TranscriptionInputPolicy.Validate(request.Inputs[0], _options);
        if (inputFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request.JobId, inputFailure, "Audio input is not allowed."));
        }

        return Task.FromResult(TimedTextNormalizer.Normalize(request.JobId, fixture, _options));
    }
}
