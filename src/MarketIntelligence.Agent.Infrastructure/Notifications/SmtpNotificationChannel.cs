using System.Net;
using System.Net.Mail;
using MarketIntelligence.Agent.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Notifications;

/// <summary>
/// SMTP email notification channel. Sends email via configured SMTP server.
/// </summary>
public sealed class SmtpNotificationChannel : INotificationChannel
{
    private readonly NotificationOptions _options;
    private readonly ILogger<SmtpNotificationChannel> _logger;

    public SmtpNotificationChannel(
        IOptions<NotificationOptions> options,
        ILogger<SmtpNotificationChannel> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured =>
        _options.Enabled &&
        _options.Smtp is not null &&
        !string.IsNullOrWhiteSpace(_options.Smtp.Host) &&
        !string.IsNullOrWhiteSpace(_options.Smtp.FromAddress) &&
        _options.Smtp.Recipients.Count > 0;

    public async Task<NotificationResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var notificationId = GenerateNotificationId();

        if (!IsConfigured)
        {
            return NotificationResult.Failed(notificationId, "notification_not_configured");
        }

        var validation = ValidateMessage(message);
        if (validation is not null)
        {
            return NotificationResult.Failed(notificationId, validation);
        }

        var recipientValidation = await ValidateRecipientsAsync(_options.Smtp!.Recipients, cancellationToken)
            .ConfigureAwait(false);
        if (recipientValidation is not null)
        {
            return NotificationResult.Failed(notificationId, recipientValidation);
        }

        if (_options.DryRun)
        {
            _logger.LogInformation(
                "DryRun: would send notification {Id} via SMTP to {Count} recipient(s). Subject: {Subject}",
                notificationId, _options.Smtp.Recipients.Count, message.Subject);
            return NotificationResult.DryRun(notificationId);
        }

        try
        {
            using var mailMessage = BuildMailMessage(message);
            using var smtpClient = CreateSmtpClient();

            await smtpClient.SendMailAsync(mailMessage, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Notification {Id} delivered via SMTP to {Count} recipient(s).",
                notificationId, _options.Smtp.Recipients.Count);
            return NotificationResult.Success(notificationId);
        }
        catch (SmtpException exception)
        {
            _logger.LogError(exception, "SMTP delivery failed. Notification {Id}.", notificationId);
            return NotificationResult.Failed(notificationId, "transient_provider_failure");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Email delivery failed unexpectedly. Notification {Id}.", notificationId);
            return NotificationResult.Failed(notificationId, "internal_error");
        }
    }

    private static string? ValidateMessage(NotificationMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Subject) || string.IsNullOrWhiteSpace(message.BodyText))
        {
            return "invalid_notification";
        }

        // 1 MB total size limit for email body.
        var estimatedSize = message.Subject.Length + message.BodyText.Length +
            (message.BodyMarkdown?.Length ?? 0);
        if (estimatedSize > 1_000_000)
        {
            return "message_too_large";
        }

        return null;
    }

    private static async Task<string?> ValidateRecipientsAsync(
        IReadOnlyList<string> recipients,
        CancellationToken cancellationToken)
    {
        foreach (var recipient in recipients)
        {
            if (!MailAddress.TryCreate(recipient, out _))
            {
                return "invalid_recipient";
            }

            if (!await SsrfGuard.IsEmailRecipientSafeAsync(recipient, cancellationToken).ConfigureAwait(false))
            {
                return "ssrf_blocked";
            }
        }

        return null;
    }

    private MailMessage BuildMailMessage(NotificationMessage message)
    {
        var mailMessage = new MailMessage
        {
            From = new MailAddress(_options.Smtp!.FromAddress!),
            Subject = message.Subject,
            Body = message.BodyText,
            IsBodyHtml = false
        };

        foreach (var recipient in _options.Smtp.Recipients)
        {
            mailMessage.To.Add(recipient);
        }

        return mailMessage;
    }

    private SmtpClient CreateSmtpClient()
    {
        var smtpClient = new SmtpClient(_options.Smtp!.Host!, _options.Smtp.Port)
        {
            EnableSsl = _options.Smtp.UseSsl,
            Timeout = _options.Smtp.TimeoutMs
        };

        if (!string.IsNullOrWhiteSpace(_options.Smtp.Username))
        {
            smtpClient.Credentials = new NetworkCredential(
                _options.Smtp.Username,
                _options.Smtp.Password ?? string.Empty);
        }

        return smtpClient;
    }

    private static string GenerateNotificationId() =>
        $"notif-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
}
