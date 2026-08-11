namespace MarketIntelligence.Agent.Application.Notifications;

/// <summary>
/// A push notification channel (email, webhook, etc.) that delivers rendered
/// messages to recipients. All channels support dry-run mode where the message
/// is rendered and validated but not actually delivered.
/// </summary>
public interface INotificationChannel
{
    /// <summary>
    /// True when the channel has the configuration required to attempt delivery.
    /// A false value means <see cref="SendAsync"/> will return
    /// <c>notification_not_configured</c> without attempting to reach any provider.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Delivers a notification message. Validation failures (oversized message,
    /// unsafe recipient) are returned as failed results rather than thrown.
    /// </summary>
    Task<NotificationResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default);
}
