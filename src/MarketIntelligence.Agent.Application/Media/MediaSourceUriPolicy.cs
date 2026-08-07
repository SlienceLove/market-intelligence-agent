using System.Net;

namespace MarketIntelligence.Agent.Application.Media;

public static class MediaSourceUriPolicy
{
    public static bool TryValidate(
        string rawUri,
        IReadOnlySet<string> allowedHosts,
        out Uri? uri,
        out string? failureCode)
    {
        uri = null;
        failureCode = null;

        if (string.IsNullOrWhiteSpace(rawUri) ||
            !Uri.TryCreate(rawUri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            failureCode = "unsupported_source_uri";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) ||
            parsed.IsLoopback ||
            IPAddress.TryParse(parsed.Host, out _))
        {
            failureCode = "private_source_uri";
            return false;
        }

        var host = parsed.Host.TrimEnd('.').ToLowerInvariant();
        if (!allowedHosts.Contains(host))
        {
            failureCode = "source_host_not_allowed";
            return false;
        }

        uri = parsed;
        return true;
    }
}
