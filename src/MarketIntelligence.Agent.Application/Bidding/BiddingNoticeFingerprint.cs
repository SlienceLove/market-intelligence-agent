using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Stable identity for a bidding notice, used by the dedupe ledger to decide
/// whether a notice has already been seen and pushed.
/// <para>
/// Stability is the whole point: the same notice fetched again — via a different
/// list page, with tracking parameters appended, or with incidental whitespace
/// differences in the title — must produce the same fingerprint, or unattended
/// scheduled pushes would re-send notices that were already delivered.
/// </para>
/// </summary>
public static class BiddingNoticeFingerprint
{
    /// <summary>
    /// Query parameters dropped before hashing because they identify a listing
    /// view or a tracking campaign rather than the notice itself.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. The two failure modes are not symmetric: an
    /// unstable fingerprint re-pushes a notice, which is visible and recoverable,
    /// while a colliding fingerprint suppresses a distinct notice silently and
    /// permanently once the ledger has recorded it. So a key is dropped only when
    /// it is near-certainly non-identity. Notably absent are <c>p</c>,
    /// <c>index</c>, and <c>from</c>: <c>p</c> is the canonical post identifier on
    /// WordPress-style sites, and the other two are equally often a record index
    /// or a date-range bound. Platform adapters that know their own URL scheme
    /// pass extra keys through <paramref name="additionalVolatileKeys"/> on
    /// <see cref="Compute(string, string, string, IReadOnlyCollection{string})"/>.
    /// </remarks>
    private static readonly string[] VolatileQueryKeys =
    [
        "page", "pageno", "pageindex", "pagesize",
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "ref", "referrer", "spm", "timestamp", "ts", "_", "rnd", "random"
    ];

    public static string Compute(string sourcePlatform, string noticeUrl, string title) =>
        Compute(sourcePlatform, noticeUrl, title, additionalVolatileKeys: null);

    /// <summary>
    /// Computes a notice fingerprint, additionally dropping query keys that the
    /// calling platform adapter knows to be non-identity for its own URL scheme.
    /// </summary>
    public static string Compute(
        string sourcePlatform,
        string noticeUrl,
        string title,
        IReadOnlyCollection<string>? additionalVolatileKeys)
    {
        var platform = NormalizePlatform(sourcePlatform);
        var url = NormalizeUrl(noticeUrl, additionalVolatileKeys);
        var normalizedTitle = NormalizeTitle(title);

        // The separator is a character that cannot appear in any normalized part,
        // so distinct inputs cannot collide by concatenation.
        var payload = string.Join('\n', platform, url, normalizedTitle);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return $"bidding:{platform}:{hex[..32]}";
    }

    private static string NormalizePlatform(string? sourcePlatform)
    {
        if (string.IsNullOrWhiteSpace(sourcePlatform))
        {
            return "unknown";
        }

        var trimmed = sourcePlatform.Trim().ToLowerInvariant();

        // Accept either a bare host or a full URL as the platform identifier.
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            trimmed = parsed.Host;
        }

        trimmed = trimmed.TrimEnd('.');
        if (trimmed.StartsWith("www.", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        return trimmed.Length == 0 ? "unknown" : trimmed;
    }

    /// <summary>
    /// Canonicalises a notice URL: lowercase scheme and host, no default port,
    /// no fragment, and volatile query parameters removed. Remaining parameters
    /// are sorted so ordering differences do not change the fingerprint.
    /// </summary>
    private static string NormalizeUrl(
        string? noticeUrl,
        IReadOnlyCollection<string>? additionalVolatileKeys)
    {
        if (string.IsNullOrWhiteSpace(noticeUrl))
        {
            return string.Empty;
        }

        var raw = noticeUrl.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            // Not a URL we can canonicalise; fold whitespace and use it verbatim
            // so the fingerprint stays deterministic rather than throwing.
            return CollapseWhitespace(raw).ToLowerInvariant();
        }

        var builder = new StringBuilder();
        builder.Append(uri.Scheme.ToLowerInvariant()).Append("://");

        var host = uri.Host.TrimEnd('.').ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
        {
            host = host[4..];
        }

        builder.Append(host);

        if (!uri.IsDefaultPort)
        {
            builder.Append(':').Append(uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        var path = uri.AbsolutePath;
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        builder.Append(path);

        var stableQuery = BuildStableQuery(uri.Query, additionalVolatileKeys);
        if (stableQuery.Length > 0)
        {
            builder.Append('?').Append(stableQuery);
        }

        return builder.ToString();
    }

    private static string BuildStableQuery(
        string query,
        IReadOnlyCollection<string>? additionalVolatileKeys)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
        }

        var volatileKeys = new HashSet<string>(VolatileQueryKeys, StringComparer.OrdinalIgnoreCase);
        if (additionalVolatileKeys is not null)
        {
            foreach (var key in additionalVolatileKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    volatileKeys.Add(key.Trim());
                }
            }
        }

        var pairs = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair =>
            {
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var key = separator < 0 ? pair : pair[..separator];
                var value = separator < 0 ? string.Empty : pair[(separator + 1)..];
                return (Key: key.Trim().ToLowerInvariant(), Value: value.Trim());
            })
            .Where(pair => pair.Key.Length > 0)
            .Where(pair => !volatileKeys.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal)
            .Select(pair => pair.Value.Length == 0 ? pair.Key : $"{pair.Key}={pair.Value}");

        return string.Join('&', pairs);
    }

    /// <summary>
    /// Normalises a title for comparison: full-width forms folded to half-width,
    /// whitespace collapsed, and case ignored. Chinese notice titles routinely
    /// differ by exactly these incidental variations between list and detail pages.
    /// </summary>
    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        // Compatibility decomposition folds full-width Latin/digits and many
        // punctuation variants onto their canonical half-width forms.
        var folded = title.Trim().Normalize(NormalizationForm.FormKC);
        return CollapseWhitespace(folded).ToLowerInvariant();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) || character == '　')
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
                continue;
            }

            builder.Append(character);
            previousWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }
}
