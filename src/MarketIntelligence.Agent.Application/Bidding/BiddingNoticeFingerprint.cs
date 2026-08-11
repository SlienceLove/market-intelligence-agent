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
    /// <summary>Query parameters that identify a listing view rather than the notice itself.</summary>
    private static readonly string[] VolatileQueryKeys =
    [
        "page", "pageno", "pageindex", "pagesize", "p", "index",
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
        "from", "ref", "referrer", "spm", "timestamp", "ts", "_", "rnd", "random"
    ];

    public static string Compute(string sourcePlatform, string noticeUrl, string title)
    {
        var platform = NormalizePlatform(sourcePlatform);
        var url = NormalizeUrl(noticeUrl);
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
    private static string NormalizeUrl(string? noticeUrl)
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

        var stableQuery = BuildStableQuery(uri.Query);
        if (stableQuery.Length > 0)
        {
            builder.Append('?').Append(stableQuery);
        }

        return builder.ToString();
    }

    private static string BuildStableQuery(string query)
    {
        if (string.IsNullOrEmpty(query) || query == "?")
        {
            return string.Empty;
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
            .Where(pair => !VolatileQueryKeys.Contains(pair.Key, StringComparer.Ordinal))
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
