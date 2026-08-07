namespace MarketIntelligence.Agent.Infrastructure.Images;

public sealed class ComfyUiOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    public string CheckpointName { get; set; } = "v1-5-pruned-emaonly.safetensors";

    public int PollIntervalMilliseconds { get; set; } = 500;

    public int TimeoutSeconds { get; set; } = 180;
}
