namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed class MediaOptions
{
    public bool Enabled { get; set; }

    public int MaxInputAssets { get; set; } = 4;

    public int MaxTextLength { get; set; } = 10_000;

    public int MaxFrames { get; set; } = 300;

    public long MaxOutputBytes { get; set; } = 50 * 1024 * 1024;
}
