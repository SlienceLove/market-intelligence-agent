namespace MarketIntelligence.Agent.Application.Images;

public sealed record ImageGenerationRequest(
    string Prompt,
    string? NegativePrompt = null,
    int Width = 256,
    int Height = 256,
    int Steps = 4,
    long? Seed = null);

public sealed record ImageGenerationResult(
    bool Succeeded,
    string? PromptId,
    string? AssetUrl,
    string? Filename,
    string? FailureCode,
    string? FailureMessage)
{
    public static ImageGenerationResult Failed(string code, string message) =>
        new(false, null, null, null, code, message);
}

public interface IImageGenerationService
{
    Task<ImageGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken cancellationToken = default);
}
