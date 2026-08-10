namespace MarketIntelligence.Agent.Infrastructure.Media;

/// <summary>
/// Bounds and source allowlist used by the HTTP media collector.
/// The allowlist is intentionally explicit: an empty list permits no source.
/// </summary>
public sealed class MediaCollectorOptions
{
    public bool Enabled { get; set; } = true;

    public List<string> AllowedHosts { get; set; } = [];

    public List<int> AllowedPorts { get; set; } = [80, 443];

    public long MaxResponseBytes { get; set; } = 50 * 1024 * 1024;

    public int MaxRedirects { get; set; } = 3;

    public TimeSpan Timeout { get; set; }

    public TimeSpan RequestTimeout { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public List<string> AllowedMediaTypes { get; set; } =
    [
        "video/*",
        "audio/*",
        "image/*",
        "application/octet-stream"
    ];

    internal TimeSpan EffectiveTimeout
    {
        get
        {
            if (Timeout > TimeSpan.Zero)
            {
                return Timeout;
            }

            if (RequestTimeout > TimeSpan.Zero)
            {
                return RequestTimeout;
            }

            return TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds, 1, 900));
        }
    }

    internal long EffectiveMaxResponseBytes => Math.Max(1, MaxResponseBytes);

    internal int EffectiveMaxRedirects => Math.Clamp(MaxRedirects, 0, 10);
}
