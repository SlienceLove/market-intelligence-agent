namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed class MediaOptions
{
    public bool Enabled { get; set; }

    public int MaxInputAssets { get; set; } = 4;

    public int MaxTextLength { get; set; } = 10_000;

    public int MaxFrames { get; set; } = 300;

    public long MaxOutputBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    /// The controlled directory that every <c>asset://</c> / <c>fixture://</c> reference
    /// resolves inside. Empty leaves FFmpeg-backed capabilities unconfigured.
    /// </summary>
    public string? AssetRoot { get; set; }

    public FfmpegOptions Ffmpeg { get; set; } = new();
}

public sealed class FfmpegOptions
{
    /// <summary>
    /// Absolute path to the ffmpeg binary. Deliberately not discovered from PATH so a
    /// shadowed binary cannot be picked up silently.
    /// </summary>
    public string? ExecutablePath { get; set; }

    public string? ProbeExecutablePath { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public int MaxStandardErrorBytes { get; set; } = 4 * 1024;

    public int MaxFrameWidth { get; set; } = 1920;

    public int MaxFrameHeight { get; set; } = 1080;

    /// <summary>
    /// Allowed drift between the composed audio and video streams.
    /// </summary>
    public TimeSpan MaxAudioVideoDrift { get; set; } = TimeSpan.FromMilliseconds(200);
}
