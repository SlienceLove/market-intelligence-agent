namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Configuration for the local, provider-neutral TTS HTTP boundary.
/// Model selection is intentionally not part of the application configuration.
/// </summary>
public sealed class TtsHttpOptions
{
    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    public string? ServiceKey { get; set; }
    public string? ServiceKeyHeaderName { get; set; } = "X-Agent-Api-Key";
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 2;
    public int MaxTextLength { get; set; } = 10_000;
    public int MaxSegmentLength { get; set; } = 800;
    public int MaxTotalDurationSeconds { get; set; } = 600;
    public string? OutputFormat { get; set; } = "wav";
    public int SampleRate { get; set; } = 16_000;
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);
    public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
}
