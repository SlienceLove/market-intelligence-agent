using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Media;

public sealed record MediaPathResolution(string? FullPath, string? FailureCode)
{
    public bool Succeeded => FailureCode is null && !string.IsNullOrEmpty(FullPath);

    public static MediaPathResolution Ok(string fullPath) => new(fullPath, null);

    public static MediaPathResolution Fail(string failureCode) => new(null, failureCode);
}

/// <summary>
/// The single translation point between controlled <c>asset://</c> / <c>fixture://</c>
/// references and real file system paths. FFmpeg is launched in-process rather than
/// behind a sidecar, so containment cannot be delegated to an external allowed-root
/// and has to be enforced here before any argument reaches a process.
/// </summary>
public interface IMediaAssetPathResolver
{
    bool IsConfigured { get; }

    MediaPathResolution ResolveInput(string uri);

    MediaPathResolution ResolveOutput(string relativePath);
}

public sealed class MediaAssetPathResolver : IMediaAssetPathResolver
{
    private static readonly string[] AllowedSchemes = ["asset", "fixture"];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private readonly MediaOptions _options;

    public MediaAssetPathResolver(IOptions<MediaOptions> options)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.AssetRoot);

    public MediaPathResolution ResolveInput(string uri)
    {
        if (!TryGetRoot(out var root, out var rootFailure))
        {
            return MediaPathResolution.Fail(rootFailure!);
        }

        if (string.IsNullOrWhiteSpace(uri) ||
            uri.Length > 2_048 ||
            uri.Any(char.IsControl) ||
            uri.Contains('\\', StringComparison.Ordinal))
        {
            return MediaPathResolution.Fail("invalid_asset_uri");
        }

        // Percent-encoding is rejected outright rather than decoded. Decoding would
        // reintroduce separators and dot segments after validation has already run.
        if (uri.Contains('%', StringComparison.Ordinal))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        // Uri collapses dot segments during parsing, so "asset://v/../../x" would
        // arrive as "v/x" and pass a post-parse check. Reject on the raw string:
        // a rewritten path is a different asset than the caller named.
        if (HasDotSegment(uri))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return MediaPathResolution.Fail("invalid_asset_uri");
        }

        if (!AllowedSchemes.Contains(parsed.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            return MediaPathResolution.Fail("unsupported_source_uri");
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        // asset://video/demo puts "video" in Host and "/demo" in AbsolutePath.
        var relative = string.Concat(parsed.Host, parsed.AbsolutePath);
        return Combine(root!, relative);
    }

    public MediaPathResolution ResolveOutput(string relativePath)
    {
        if (!TryGetRoot(out var root, out var rootFailure))
        {
            return MediaPathResolution.Fail(rootFailure!);
        }

        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.Length > 2_048 ||
            relativePath.Any(char.IsControl))
        {
            return MediaPathResolution.Fail("invalid_output_asset");
        }

        if (relativePath.Contains('%', StringComparison.Ordinal))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        if (Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':', StringComparison.Ordinal))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        return Combine(root!, relativePath.Replace('\\', '/'));
    }

    /// <summary>
    /// Looks for a "." or ".." path segment in the raw text, before any normalization.
    /// </summary>
    private static bool HasDotSegment(string value)
    {
        var authorityStart = value.IndexOf("//", StringComparison.Ordinal);
        var scan = authorityStart >= 0 ? value[(authorityStart + 2)..] : value;

        foreach (var segment in scan.Split(['/', '\\'], StringSplitOptions.None))
        {
            if (segment is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    private bool TryGetRoot(out string? root, out string? failureCode)
    {
        root = null;
        failureCode = null;

        if (!IsConfigured)
        {
            failureCode = "provider_not_configured";
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(_options.AssetRoot!);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failureCode = "provider_not_configured";
            return false;
        }

        if (!Directory.Exists(full))
        {
            failureCode = "provider_not_configured";
            return false;
        }

        // Resolve the root itself so a linked root compares against its real target.
        root = ResolveFinalPath(full);
        return true;
    }

    private static MediaPathResolution Combine(string root, string relative)
    {
        var segments = relative.Split('/', StringSplitOptions.None);
        var cleaned = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                return MediaPathResolution.Fail("unsafe_asset_reference");
            }

            // A leading dash lets FFmpeg reinterpret a path as an option.
            if (segment.StartsWith('-'))
            {
                return MediaPathResolution.Fail("unsafe_asset_reference");
            }

            if (segment.EndsWith('.') || segment.EndsWith(' '))
            {
                return MediaPathResolution.Fail("unsafe_asset_reference");
            }

            if (segment.Contains(':', StringComparison.Ordinal) ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return MediaPathResolution.Fail("unsafe_asset_reference");
            }

            var bare = segment.Contains('.', StringComparison.Ordinal)
                ? segment[..segment.IndexOf('.', StringComparison.Ordinal)]
                : segment;
            if (ReservedDeviceNames.Contains(bare))
            {
                return MediaPathResolution.Fail("unsafe_asset_reference");
            }

            cleaned.Add(segment);
        }

        if (cleaned.Count == 0)
        {
            return MediaPathResolution.Fail("invalid_asset_uri");
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, Path.Combine([.. cleaned])));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        if (!IsContained(root, candidate))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        // The lexical check above cannot see links. Re-check the deepest existing
        // ancestor after link resolution so a link inside the root cannot point out.
        var realized = ResolveFinalPath(candidate);
        if (!IsContained(root, realized))
        {
            return MediaPathResolution.Fail("unsafe_asset_reference");
        }

        return MediaPathResolution.Ok(candidate);
    }

    private static bool IsContained(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedRoot, candidate, comparison))
        {
            return true;
        }

        // The separator matters: without it "root-evil" passes a "root" prefix test.
        return candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    /// <summary>
    /// Resolves links component by component, from the filesystem root down to the leaf.
    /// Resolving only the leaf is not enough: the OS traverses a directory link
    /// transparently, so <c>root/link/file</c> reports as existing and the leaf reports
    /// as "not a link" even when <c>link</c> points outside the root. Every ancestor has
    /// to be resolved for containment to mean anything. Output paths legitimately do not
    /// exist yet, so absence is not treated as a failure.
    /// </summary>
    private static string ResolveFinalPath(string path)
    {
        var segments = new List<string>();
        var current = path;

        for (var depth = 0; depth < 256; depth++)
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            segments.Add(Path.GetFileName(current));
            current = parent;
        }

        segments.Reverse();

        var resolved = current;
        foreach (var segment in segments)
        {
            try
            {
                resolved = ResolveLinkChain(Path.GetFullPath(Path.Combine(resolved, segment)));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
            {
                return path;
            }
        }

        return resolved;
    }

    private static string ResolveLinkChain(string path)
    {
        var current = path;

        for (var depth = 0; depth < 16; depth++)
        {
            FileSystemInfo? target;
            try
            {
                target = Directory.Exists(current)
                    ? new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                    : new FileInfo(current).ResolveLinkTarget(returnFinalTarget: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return current;
            }

            if (target is null)
            {
                return current;
            }

            current = target.FullName;
        }

        return current;
    }
}
