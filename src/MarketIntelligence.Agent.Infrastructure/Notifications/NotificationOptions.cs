namespace MarketIntelligence.Agent.Infrastructure.Notifications;

/// <summary>
/// Configuration for notification delivery. Defaults to DryRun=true and Enabled=false
/// to prevent accidental notification sends during development.
/// </summary>
public sealed record NotificationOptions
{
    /// <summary>
    /// Master switch: if false, all notification attempts return not_configured.
    /// Defaults to false.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// DryRun mode: render and log notifications but don't actually send them.
    /// Defaults to true for safety.
    /// </summary>
    public bool DryRun { get; init; } = true;

    /// <summary>
    /// Webhook-specific configuration (URL, timeout, auth).
    /// </summary>
    public WebhookOptions? Webhook { get; init; }

    /// <summary>
    /// SMTP-specific configuration (host, port, credentials, recipients).
    /// </summary>
    public SmtpOptions? Smtp { get; init; }
}

public sealed record WebhookOptions
{
    /// <summary>
    /// Webhook URL (must be HTTPS).
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Optional bearer token for Authorization header.
    /// </summary>
    public string? BearerToken { get; init; }

    /// <summary>
    /// HTTP request timeout in milliseconds. Defaults to 10 seconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 10_000;
}

public sealed record SmtpOptions
{
    /// <summary>
    /// SMTP server host (e.g., smtp.gmail.com).
    /// </summary>
    public string? Host { get; init; }

    /// <summary>
    /// SMTP server port. Defaults to 587 (STARTTLS).
    /// </summary>
    public int Port { get; init; } = 587;

    /// <summary>
    /// Enable SSL/TLS. Defaults to true.
    /// </summary>
    public bool UseSsl { get; init; } = true;

    /// <summary>
    /// SMTP authentication username (optional for unauthenticated servers).
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// SMTP authentication password (optional).
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// From address for outgoing emails.
    /// </summary>
    public string? FromAddress { get; init; }

    /// <summary>
    /// List of recipient email addresses.
    /// </summary>
    public IReadOnlyList<string> Recipients { get; init; } = [];

    /// <summary>
    /// SMTP operation timeout in milliseconds. Defaults to 30 seconds.
    /// </summary>
    public int TimeoutMs { get; init; } = 30_000;
}
