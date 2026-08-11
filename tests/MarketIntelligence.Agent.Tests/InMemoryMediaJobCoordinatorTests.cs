using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketIntelligence.Agent.Tests;

public sealed class InMemoryMediaJobCoordinatorTests
{
    [Fact]
    public async Task Coordinator_accepts_and_completes_queued_collection_job()
    {
        var coordinator = new InMemoryMediaJobCoordinator(
            new FakeChannelMediaCollector(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" }),
            new UnconfiguredTranscriptionService(),
            new UnconfiguredFrameOcrService(),
            new UnconfiguredSpeechSynthesisService(),
            new UnconfiguredVideoCompositionService(),
            NullLogger<InMemoryMediaJobCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        try
        {
            var request = new MediaJobRequest(
                "coordinator-1",
                MediaJobKind.Collection,
                [new MediaAssetReference("https://approved.example/video", "text/uri-list")],
                CorrelationId: "correlation-1",
                IdempotencyKey: "idempotency-1");

            var accepted = await coordinator.SubmitAsync(request);
            var completed = await WaitForTerminalResultAsync(coordinator, request.JobId);

            Assert.Equal(MediaJobStatus.Accepted, accepted.Status);
            Assert.Equal(MediaJobStatus.Succeeded, completed.Status);
            Assert.Equal("correlation-1", completed.CorrelationId);
            Assert.Equal("idempotency-1", completed.IdempotencyKey);
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Coordinator_deduplicates_concurrent_idempotent_submissions()
    {
        var coordinator = new InMemoryMediaJobCoordinator(
            new FakeChannelMediaCollector(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "approved.example" }),
            new UnconfiguredTranscriptionService(),
            new UnconfiguredFrameOcrService(),
            new UnconfiguredSpeechSynthesisService(),
            new UnconfiguredVideoCompositionService(),
            NullLogger<InMemoryMediaJobCoordinator>.Instance);
        await coordinator.StartAsync(CancellationToken.None);

        try
        {
            var firstRequest = new MediaJobRequest(
                "coordinator-idem-1",
                MediaJobKind.Collection,
                [new MediaAssetReference("https://approved.example/video", "text/uri-list")],
                IdempotencyKey: "same-idempotency");
            var secondRequest = firstRequest with { JobId = "coordinator-idem-2" };

            var results = await Task.WhenAll(
                coordinator.SubmitAsync(firstRequest),
                coordinator.SubmitAsync(secondRequest));

            Assert.Equal(results[0].JobId, results[1].JobId);
            Assert.NotNull(coordinator.Get(results[0].JobId));
        }
        finally
        {
            await coordinator.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<MediaJobResult> WaitForTerminalResultAsync(
        IMediaJobCoordinator coordinator,
        string jobId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var result = coordinator.Get(jobId);
            if (result?.IsTerminal == true)
            {
                return result;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The coordinator did not complete the media job.");
    }
}
