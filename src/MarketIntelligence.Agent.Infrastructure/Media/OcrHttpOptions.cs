using MarketIntelligence.Agent.Application.Media;

namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed class OcrHttpOptions
{
    public bool Enabled { get; set; }

    public string? Endpoint { get; set; }

    public string? ApiKey { get; set; }

    public string? ApiKeyHeaderName { get; set; }

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    public long MaxResponseBytes { get; set; } = 2 * 1024 * 1024;

    public FrameOcrOptions Ocr { get; set; } = new();
}
