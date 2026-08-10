namespace MarketIntelligence.Agent.Application.Media;

public sealed class FakeFrameOcrService(
    IReadOnlyList<OcrFrameText> fixture,
    FrameOcrOptions? options = null) : IFrameOcrService
{
    private readonly FrameOcrOptions _options = options ?? new();

    public Task<MediaJobResult> RecognizeAsync(
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
            return Task.FromResult(MediaJobResult.Failed(request, requestFailure, "OCR request is invalid."));
        }

        if (request.Kind != MediaJobKind.FrameOcr)
        {
            return Task.FromResult(MediaJobResult.Failed(
                request,
                "unsupported_media_job",
                "OCR service only accepts frame OCR jobs."));
        }

        var inputFailure = FrameOcrInputPolicy.Validate(request.Inputs[0], _options);
        if (inputFailure is not null)
        {
            return Task.FromResult(MediaJobResult.Failed(request, inputFailure, "OCR input is not allowed."));
        }

        var frames = OcrResultNormalizer.Normalize(fixture, _options);
        if (frames.Count == 0)
        {
            return Task.FromResult(MediaJobResult.Failed(
                request,
                "empty_ocr_result",
                "OCR returned no usable frames."));
        }

        return Task.FromResult(new MediaJobResult(
            request.JobId,
            MediaJobStatus.Succeeded,
            CorrelationId: request.CorrelationId,
            IdempotencyKey: request.IdempotencyKey,
            OcrFrames: frames));
    }
}
