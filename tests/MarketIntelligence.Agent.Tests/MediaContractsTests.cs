using MarketIntelligence.Agent.Application.Media;

namespace MarketIntelligence.Agent.Tests;

public sealed class MediaContractsTests
{
    [Fact]
    public void Request_carries_correlation_and_idempotency_context_through_lifecycle()
    {
        var request = new MediaJobRequest(
            "job-contract-1",
            MediaJobKind.Collection,
            [new MediaAssetReference("asset://fixture/input", "video/mp4")],
            CorrelationId: "corr-1",
            IdempotencyKey: "idem-1");

        var accepted = request.Accepted();
        var running = request.Running();
        var succeeded = MediaJobResult.Succeeded(request);

        Assert.Null(request.Validate());
        Assert.Equal(MediaJobStatus.Accepted, accepted.Status);
        Assert.Equal(MediaJobStatus.Running, running.Status);
        Assert.Equal("corr-1", accepted.CorrelationId);
        Assert.Equal("idem-1", running.IdempotencyKey);
        Assert.Equal(request.CorrelationId, succeeded.CorrelationId);
        Assert.Equal(request.IdempotencyKey, succeeded.IdempotencyKey);
    }

    [Fact]
    public void Lifecycle_allows_only_forward_workflow_transitions()
    {
        Assert.True(MediaJobStatus.Accepted.CanTransitionTo(MediaJobStatus.Running));
        Assert.True(MediaJobStatus.Running.CanTransitionTo(MediaJobStatus.Succeeded));
        Assert.True(MediaJobStatus.Accepted.CanTransitionTo(MediaJobStatus.Cancelled));
        Assert.False(MediaJobStatus.Succeeded.CanTransitionTo(MediaJobStatus.Running));

        var accepted = MediaJobResult.Accepted("job-contract-2", "corr-2", "idem-2");
        Assert.True(MediaJobLifecycle.TryTransition(accepted, MediaJobStatus.Running, out var running));
        Assert.Equal(MediaJobStatus.Running, running.Status);
        Assert.Equal("corr-2", running.CorrelationId);

        Assert.False(MediaJobLifecycle.TryTransition(running, MediaJobStatus.Accepted, out var rejected));
        Assert.Equal("invalid_status_transition", rejected.FailureCode);
        Assert.Equal(MediaFailureCategory.Conflict, rejected.ErrorCategory);
    }

    [Fact]
    public void Request_rejects_unsafe_references_and_unbounded_metadata_before_provider_call()
    {
        var fileRequest = new MediaJobRequest(
            "job-contract-3",
            MediaJobKind.Collection,
            [new MediaAssetReference("file:///var/tmp/input.mp4", "video/mp4")]);
        var credentialsRequest = fileRequest with
        {
            Inputs = [new MediaAssetReference("https://user:secret@approved.example/input", "video/mp4")]
        };
        var oversizedKeyRequest = fileRequest with
        {
            Inputs = [new MediaAssetReference("asset://fixture/input", "video/mp4")],
            IdempotencyKey = new string('k', MediaContractLimits.MaxIdempotencyKeyCharacters + 1)
        };

        Assert.Equal("unsupported_source_uri", fileRequest.Validate());
        Assert.Equal("private_source_uri", credentialsRequest.Validate());
        Assert.Equal("invalid_idempotency_key", oversizedKeyRequest.Validate());
    }

    [Fact]
    public void Request_rejects_invalid_parameters_and_timed_text_rejects_nan_confidence()
    {
        var invalidParameters = new MediaJobRequest(
            "job-contract-4",
            MediaJobKind.Transcription,
            [new MediaAssetReference("asset://fixture/audio", "audio/wav")],
            Parameters: new Dictionary<string, string> { ["unsafe\nkey"] = "value" });
        var invalidText = new TimedTextSegment(
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1),
            "fixture",
            double.NaN);

        Assert.Equal("invalid_parameter", invalidParameters.Validate());
        Assert.Equal("invalid_confidence", invalidText.Validate());
        Assert.False(invalidText.IsValid);
    }

    [Theory]
    [InlineData("provider_not_configured", MediaFailureCategory.ProviderUnavailable, false)]
    [InlineData("rate_limited", MediaFailureCategory.RateLimited, true)]
    [InlineData("composition_timeout", MediaFailureCategory.Timeout, true)]
    [InlineData("job_conflict", MediaFailureCategory.Conflict, false)]
    [InlineData("private_source_uri", MediaFailureCategory.Security, false)]
    [InlineData("invalid_input_asset", MediaFailureCategory.Validation, false)]
    public void Failure_codes_are_classified_and_retryability_is_explicit(
        string code,
        MediaFailureCategory category,
        bool retryable)
    {
        Assert.Equal(category, MediaFailureCatalog.Classify(code));
        Assert.Equal(retryable, MediaFailureCatalog.IsRetryable(code));
    }

    [Fact]
    public void Failed_result_redacts_external_references_and_preserves_context()
    {
        var result = MediaJobResult.Failed(
            "job-contract-5",
            "provider_unavailable",
            "provider https://service.example/token=secret failed",
            "corr-5",
            "idem-5");

        Assert.Equal(MediaJobStatus.Failed, result.Status);
        Assert.Equal(MediaFailureCategory.ProviderUnavailable, result.ErrorCategory);
        Assert.Equal("corr-5", result.CorrelationId);
        Assert.Equal("idem-5", result.IdempotencyKey);
        Assert.DoesNotContain("https://", result.FailureMessage, StringComparison.Ordinal);
        Assert.Null(result.Validate());
    }
}
