namespace MarketIntelligence.Agent.Application.Media;

public sealed class UnconfiguredChannelMediaCollector : IChannelMediaCollector
{
    public Task<MediaJobResult> CollectAsync(MediaJobRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(Unconfigured(request));

    private static MediaJobResult Unconfigured(MediaJobRequest request) =>
        MediaJobResult.Failed(request.JobId, "provider_not_configured", "Media collection provider is not configured.");
}

public sealed class UnconfiguredTranscriptionService : ITranscriptionService
{
    public Task<MediaJobResult> TranscribeAsync(MediaJobRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(MediaJobResult.Failed(request.JobId, "provider_not_configured", "ASR provider is not configured."));
}

public sealed class UnconfiguredFrameOcrService : IFrameOcrService
{
    public Task<MediaJobResult> RecognizeAsync(MediaJobRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(MediaJobResult.Failed(request.JobId, "provider_not_configured", "OCR provider is not configured."));
}

public sealed class UnconfiguredSpeechSynthesisService : ISpeechSynthesisService
{
    public Task<MediaJobResult> SynthesizeAsync(MediaJobRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(MediaJobResult.Failed(request.JobId, "provider_not_configured", "TTS provider is not configured."));
}

public sealed class UnconfiguredVideoCompositionService : IVideoCompositionService
{
    public Task<MediaJobResult> ComposeAsync(MediaJobRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(MediaJobResult.Failed(request.JobId, "provider_not_configured", "Video composition provider is not configured."));
}
