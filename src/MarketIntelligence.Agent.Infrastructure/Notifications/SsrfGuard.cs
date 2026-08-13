using System.Net;
using System.Net.Sockets;

namespace MarketIntelligence.Agent.Infrastructure.Notifications;

/// <summary>
/// Validates URLs and email addresses against SSRF (Server-Side Request Forgery)
/// attacks. Rejects private networks, loopback, link-local, IP literals, and
/// non-HTTPS webhooks so a notification cannot be weaponized to probe internal
/// infrastructure or exfiltrate data to an attacker-controlled endpoint.
/// </summary>
public static class SsrfGuard
{
    private static readonly string[] PrivateV4Prefixes =
    [
        "10.", "172.16.", "172.17.", "172.18.", "172.19.", "172.20.", "172.21.",
        "172.22.", "172.23.", "172.24.", "172.25.", "172.26.", "172.27.", "172.28.",
        "172.29.", "172.30.", "172.31.", "192.168.", "169.254."
    ];

    /// <summary>
    /// True when the URL points to a public HTTPS endpoint reachable via a domain
    /// name (not an IP literal) and not in a private, loopback, or link-local range.
    /// </summary>
    public static bool IsWebhookUrlSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // Only HTTPS allowed for webhooks.
        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // IP literals (both v4 and v6) are rejected.
        if (IPAddress.TryParse(uri.Host, out _))
        {
            return false;
        }

        // Resolve the domain and check every returned address.
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            return addresses.All(IsPublicAddress);
        }
        catch
        {
            // DNS resolution failure: treat as unsafe rather than allowing it through.
            return false;
        }
    }

    /// <summary>
    /// True when the email address domain does not resolve to a private or loopback
    /// address. The SMTP server itself may be internal, but the recipient domain
    /// must be resolvable to a public address.
    /// </summary>
    public static bool IsEmailRecipientSafe(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var atIndex = email.IndexOf('@', StringComparison.Ordinal);
        if (atIndex < 1 || atIndex == email.Length - 1)
        {
            return false;
        }

        var domain = email[(atIndex + 1)..];

        // Localhost and IP literals in the domain part are rejected.
        if (domain.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(domain, out _))
        {
            return false;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(domain);
            return addresses.All(IsPublicAddress);
        }
        catch
        {
            // DNS resolution failure for email domain: allow it through since the
            // SMTP server will reject it anyway, and false-blocking legitimate
            // domains is worse than a DNS timeout.
            return true;
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            // Link-local (fe80::/10)
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return false;
            }

            // Unique local (fc00::/7)
            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return false;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var text = address.ToString();

            foreach (var prefix in PrivateV4Prefixes)
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            // Loopback 127.0.0.0/8 is already caught by IPAddress.IsLoopback.
        }

        return true;
    }

    /// <summary>
    /// True when the URI points to a public endpoint (http or https) reachable via a
    /// domain name (not an IP literal) and not in a private, loopback, or link-local
    /// range. Use for outbound HTTP collection (bidding notices, robots.txt) where both
    /// HTTP and HTTPS are legitimate schemes but internal-network access must still be
    /// blocked.
    /// </summary>
    public static bool IsCollectionUrlSafe(Uri uri)
    {
        if (uri is null)
        {
            return false;
        }

        // Only HTTP and HTTPS are permitted for collection requests.
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // IP literals (both v4 and v6) are rejected.
        if (IPAddress.TryParse(uri.Host, out _))
        {
            return false;
        }

        // Resolve the domain and check every returned address.
        try
        {
            var addresses = Dns.GetHostAddresses(uri.Host);
            return addresses.All(IsPublicAddress);
        }
        catch
        {
            // DNS resolution failure: treat as unsafe rather than allowing it through.
            return false;
        }
    }

    /// <inheritdoc cref="IsCollectionUrlSafe(Uri)"/>
    public static bool IsCollectionUrlSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsCollectionUrlSafe(uri);
    }

    /// <summary>
    /// Validates an email recipient to prevent SSRF via SMTP relay. Checks that the
    /// domain resolves to public IPs only (no private, loopback, or link-local).
    /// </summary>
    public static async Task<bool> IsEmailRecipientSafeAsync(
        string emailAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return false;
        }

        if (!System.Net.Mail.MailAddress.TryCreate(emailAddress, out var mailAddress))
        {
            return false;
        }

        var domain = mailAddress.Host;

        try
        {
            var hostEntry = await Dns.GetHostEntryAsync(domain, cancellationToken).ConfigureAwait(false);

            foreach (var address in hostEntry.AddressList)
            {
                if (IsPublicAddress(address))
                {
                    return true; // At least one public IP found — accept
                }
            }

            // All resolved IPs are private/local — reject
            return false;
        }
        catch (Exception)
        {
            // DNS resolution failed — reject to fail closed.
            return false;
        }
    }
}
