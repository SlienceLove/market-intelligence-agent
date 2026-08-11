namespace MarketIntelligence.Agent.Application.Notifications;

/// <summary>
/// A rendered notification carrying bidding notices or other collected
/// intelligence, ready to be delivered through a push channel.
/// </summary>
public sealed record NotificationMessage
{
    public required string Subject { get; init; }

    /// <summary>
    /// Plain-text body, always present and suitable for SMTP plain-text mode
    /// or fallback rendering.
    /// </summary>
    public required string BodyText { get; init; }

    /// <summary>
    /// Optional Markdown body for channels that support richer formatting
    /// (webhooks with Markdown support, HTML email).
    /// </summary>
    public string? BodyMarkdown { get; init; }

    /// <summary>
    /// Individual notice items included in this notification, used for
    /// structured rendering by channels that support card or list views.
    /// </summary>
    public IReadOnlyList<NotificationItem> Items { get; init; } = [];

    public required DateTimeOffset GeneratedAt { get; init; }
}

public sealed record NotificationItem
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public string? Publisher { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Region { get; init; }
    public string? AmountRange { get; init; }
}

public enum NotificationStatus
{
    Pending,
    Delivered,
    Failed,
    DryRun,
    Cancelled
}

public enum NotificationFailureCategory
{
    None,
    Validation,
    Security,
    Authorization,
    ProviderUnavailable,
    RateLimited,
    Timeout,
    Transient,
    Internal,
    Unknown
}

public sealed record NotificationResult
{
    public required string NotificationId { get; init; }
    public required NotificationStatus Status { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public NotificationFailureCategory ErrorCategory { get; init; }
    public bool IsDryRun { get; init; }

    public bool Succeeded => Status is NotificationStatus.Delivered or NotificationStatus.DryRun;

    public static NotificationResult Success(string notificationId) =>
        new()
        {
            NotificationId = notificationId,
            Status = NotificationStatus.Delivered,
            ErrorCategory = NotificationFailureCategory.None
        };

    public static NotificationResult DryRun(string notificationId) =>
        new()
        {
            NotificationId = notificationId,
            Status = NotificationStatus.DryRun,
            IsDryRun = true,
            ErrorCategory = NotificationFailureCategory.None
        };

    public static NotificationResult Failed(
        string notificationId,
        string failureCode,
        string? failureMessage = null) =>
        new()
        {
            NotificationId = notificationId,
            Status = NotificationStatus.Failed,
            FailureCode = failureCode,
            FailureMessage = NotificationFailureCatalog.SanitizeMessage(failureCode, failureMessage),
            ErrorCategory = NotificationFailureCatalog.Classify(failureCode)
        };

    public static NotificationResult Cancelled(string notificationId) =>
        new()
        {
            NotificationId = notificationId,
            Status = NotificationStatus.Cancelled,
            ErrorCategory = NotificationFailureCategory.None
        };
}

public static class NotificationFailureCatalog
{
    private static readonly IReadOnlyDictionary<string, NotificationFailureCategory> Categories =
        new Dictionary<string, NotificationFailureCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["notification_id_required"] = NotificationFailureCategory.Validation,
            ["invalid_notification"] = NotificationFailureCategory.Validation,
            ["message_too_large"] = NotificationFailureCategory.Validation,
            ["invalid_recipient"] = NotificationFailureCategory.Validation,
            ["notification_not_configured"] = NotificationFailureCategory.ProviderUnavailable,
            ["provider_not_configured"] = NotificationFailureCategory.ProviderUnavailable,
            ["channel_not_configured"] = NotificationFailureCategory.ProviderUnavailable,
            ["notification_rejected"] = NotificationFailureCategory.ProviderUnavailable,
            ["ssrf_blocked"] = NotificationFailureCategory.Security,
            ["unsafe_recipient"] = NotificationFailureCategory.Security,
            ["private_address"] = NotificationFailureCategory.Security,
            ["unauthorized"] = NotificationFailureCategory.Authorization,
            ["forbidden"] = NotificationFailureCategory.Authorization,
            ["rate_limited"] = NotificationFailureCategory.RateLimited,
            ["timeout"] = NotificationFailureCategory.Timeout,
            ["transient_provider_failure"] = NotificationFailureCategory.Transient,
            ["cancelled"] = NotificationFailureCategory.None,
            ["internal_error"] = NotificationFailureCategory.Internal
        };

    public static NotificationFailureCategory Classify(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return NotificationFailureCategory.None;
        }

        var normalized = failureCode.Trim();
        if (Categories.TryGetValue(normalized, out var category))
        {
            return category;
        }

        if (normalized.StartsWith("invalid_", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("_required", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationFailureCategory.Validation;
        }

        if (normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationFailureCategory.Timeout;
        }

        return NotificationFailureCategory.Unknown;
    }

    public static string? SanitizeMessage(string? failureCode, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = failureCode?.Trim() ?? string.Empty;

        // Credentials, URLs, and file paths must never cross the application boundary.
        if (normalized.Contains("ssrf", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("webhook", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("smtp", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("recipient", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("rejected", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("://", StringComparison.Ordinal) ||
            message.Contains("@", StringComparison.Ordinal) ||
            message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return SafeDefaultMessage(normalized);
        }

        return message.Length > 512 ? message[..512] : message;
    }

    public static string SafeDefaultMessage(string failureCode) =>
        failureCode switch
        {
            "notification_not_configured" => "Notification channel is not configured.",
            "ssrf_blocked" => "Recipient address rejected by security policy.",
            "unsafe_recipient" => "Recipient address is not allowed.",
            "private_address" => "Recipient points to a private network address.",
            "notification_rejected" => "Notification provider rejected the message.",
            _ => "Notification delivery failed."
        };
}
