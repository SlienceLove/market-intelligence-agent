using System.Globalization;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Media;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed record MediaDurations(
    TimeSpan? Container,
    TimeSpan? Video,
    TimeSpan? Audio)
{
    /// <summary>
    /// Absolute gap between the audio and video streams. Null when either stream is
    /// missing, since a single-stream file cannot drift.
    /// </summary>
    public TimeSpan? Drift =>
        Video is { } video && Audio is { } audio
            ? (video > audio ? video - audio : audio - video)
            : null;
}

public interface IMediaProbe
{
    bool IsConfigured { get; }

    Task<MediaDurations?> ProbeAsync(string fullPath, CancellationToken cancellationToken = default);
}

public sealed class FfprobeMediaProbe : IMediaProbe
{
    private readonly IProcessRunner _processRunner;
    private readonly MediaOptions _options;

    public FfprobeMediaProbe(IProcessRunner processRunner, IOptions<MediaOptions> options)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.Ffmpeg.ProbeExecutablePath);

    public async Task<MediaDurations?> ProbeAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        var request = new ProcessRunRequest(
            "ffprobe",
            [
                "-hide_banner", "-loglevel", "error",
                "-show_entries", "format=duration:stream=codec_type,duration",
                "-of", "json",
                fullPath
            ],
            _options.Ffmpeg.ProbeTimeout > TimeSpan.Zero
                ? _options.Ffmpeg.ProbeTimeout
                : TimeSpan.FromSeconds(30));

        var result = await _processRunner.RunAsync(request, cancellationToken);
        if (result.Cancelled || result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        return Parse(result.StandardOutput);
    }

    private static MediaDurations? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            TimeSpan? container = null;
            if (root.TryGetProperty("format", out var format) &&
                format.ValueKind == JsonValueKind.Object &&
                TryDuration(format, out var containerDuration))
            {
                container = containerDuration;
            }

            TimeSpan? video = null;
            TimeSpan? audio = null;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (stream.ValueKind != JsonValueKind.Object ||
                        !stream.TryGetProperty("codec_type", out var codecType) ||
                        codecType.ValueKind != JsonValueKind.String ||
                        !TryDuration(stream, out var duration))
                    {
                        continue;
                    }

                    // Keep the longest stream of each type: a file may carry several.
                    switch (codecType.GetString())
                    {
                        case "video" when video is null || duration > video:
                            video = duration;
                            break;
                        case "audio" when audio is null || duration > audio:
                            audio = duration;
                            break;
                    }
                }
            }

            return new MediaDurations(container, video, audio);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryDuration(JsonElement element, out TimeSpan duration)
    {
        duration = default;

        if (!element.TryGetProperty("duration", out var property))
        {
            return false;
        }

        // ffprobe reports duration as a string, and "N/A" is a normal value.
        double seconds;
        switch (property.ValueKind)
        {
            case JsonValueKind.String:
                if (!double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
                {
                    return false;
                }
                break;
            case JsonValueKind.Number:
                if (!property.TryGetDouble(out seconds))
                {
                    return false;
                }
                break;
            default:
                return false;
        }

        if (!double.IsFinite(seconds) || seconds < 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
