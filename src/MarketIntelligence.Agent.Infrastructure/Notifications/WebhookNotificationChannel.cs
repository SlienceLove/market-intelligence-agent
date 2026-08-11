using System.Net.Http.Json;
using System.Text.Json;
using MarketIntelligence.Agent.Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Notifications;

/// <summary>
/// Webhook notification channel, typically for group chat bots (DingTalk,
/// WeCom, Slack, etc.). Posts a JSON payload to the configured webhook URL.
/// </summary>
public sealed class WebhookNotificationChannel : INotificationChannel
{
    private readonly NotificationOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookNotificationChannel> _logger;

    public WebhookNotificationChannel(
        IOptions<NotificationOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookNotificationChannel> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured =>
        _options.Enabled &&
        _options.Webhook is not null &&
        !string.IsNullOrWhiteSpace(_options.Webhook.Url);

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

        var webhookUrl = _options.Webhook!.Url!;

        if (!SsrfGuard.IsWebhookUrlSafe(webhookUrl))
        {
            _logger.LogWarning("Webhook URL {Url} rejected by SSRF guard.", webhookUrl);
            return NotificationResult.Failed(notificationId, "ssrf_blocked");
        }

        if (_options.DryRun)
        {
            _logger.LogInformation(
                "DryRun: would POST notification {Id} to {Url}. Subject: {Subject}, Items: {Count}",
                notificationId, webhookUrl, message.Subject, message.Items.Count);
            return NotificationResult.DryRun(notificationId);
        }

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMilliseconds(_options.Webhook.TimeoutMs);

            var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = JsonContent.Create(BuildPayload(message))
            };

            if (!string.IsNullOrWhiteSpace(_options.Webhook.BearerToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", _options.Webhook.BearerToken);
            }

            var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Webhook {Url} returned {StatusCode}. Notification {Id} not delivered.",
                    webhookUrl, response.StatusCode, notificationId);
                return NotificationResult.Failed(notificationId, "notification_rejected");
            }

            _logger.LogInformation("Notification {Id} delivered via webhook {Url}.", notificationId, webhookUrl);
            return NotificationResult.Success(notificationId);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Webhook {Url} HTTP request failed. Notification {Id} not delivered.", webhookUrl, notificationId);
            return NotificationResult.Failed(notificationId, "transient_provider_failure");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Webhook {Url} timed out. Notification {Id} not delivered.", webhookUrl, notificationId);
            return NotificationResult.Failed(notificationId, "timeout");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Webhook {Url} delivery failed unexpectedly. Notification {Id}.", webhookUrl, notificationId);
            return NotificationResult.Failed(notificationId, "internal_error");
        }
    }

    private static string? ValidateMessage(NotificationMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Subject) || string.IsNullOrWhiteSpace(message.BodyText))
        {
            return "invalid_notification";
        }

        // 100 KB payload limit to prevent abuse.
        var estimatedSize = message.Subject.Length + message.BodyText.Length +
            (message.BodyMarkdown?.Length ?? 0);
        if (estimatedSize > 100_000)
        {
            return "message_too_large";
        }

        return null;
    }

    private static object BuildPayload(NotificationMessage message)
    {
        // Generic JSON structure. Actual webhook providers (DingTalk, WeCom) have
        // their own schemas; this is a minimal common structure for testing.
        return new
        {
            msgtype = "markdown",
            markdown = new
            {
                title = message.Subject,
                text = message.BodyMarkdown ?? message.BodyText
            }
        };
    }

    private static string GenerateNotificationId() =>
        $"notif-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..32];
}
