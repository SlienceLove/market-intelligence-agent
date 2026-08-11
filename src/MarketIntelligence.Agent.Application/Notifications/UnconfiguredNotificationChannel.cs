namespace MarketIntelligence.Agent.Application.Notifications;

/// <summary>
/// Safe-failure adapter returned when no notification channel is configured.
/// Validates the message but immediately returns
/// <c>notification_not_configured</c> without attempting any delivery.
/// </summary>
public sealed class UnconfiguredNotificationChannel : INotificationChannel
{
    public bool IsConfigured => false;

    public Task<NotificationResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var validation = ValidateMessage(message);
        if (validation is not null)
        {
            return Task.FromResult(NotificationResult.Failed(
                GenerateNotificationId(),
                validation));
        }

        return Task.FromResult(NotificationResult.Failed(
            GenerateNotificationId(),
            "notification_not_configured",
            "No notification channel is configured. Check appsettings.json."));
    }

    private static string? ValidateMessage(NotificationMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Subject))
        {
            return "invalid_notification";
        }

        if (string.IsNullOrWhiteSpace(message.BodyText))
        {
            return "invalid_notification";
        }

        return null;
    }

    private static string GenerateNotificationId() =>
        $"notif-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
}
