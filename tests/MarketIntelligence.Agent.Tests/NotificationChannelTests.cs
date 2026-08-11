using MarketIntelligence.Agent.Application.Notifications;
using MarketIntelligence.Agent.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class NotificationChannelTests
{
    [Fact]
    public async Task UnconfiguredChannel_ReturnsNotConfigured()
    {
        var channel = new UnconfiguredNotificationChannel();

        Assert.False(channel.IsConfigured);

        var result = await channel.SendAsync(CreateTestMessage());

        Assert.Equal(NotificationStatus.Failed, result.Status);
        Assert.Equal("notification_not_configured", result.FailureCode);
    }

    [Fact]
    public void WebhookChannel_NotConfigured_WhenDisabled()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = false,
            Webhook = new WebhookOptions { Url = "https://example.com/hook" }
        });

        var channel = new WebhookNotificationChannel(
            options,
            new FakeHttpClientFactory(),
            NullLogger<WebhookNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);
    }

    [Fact]
    public void WebhookChannel_NotConfigured_WhenNoUrl()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = true,
            Webhook = new WebhookOptions { Url = null }
        });

        var channel = new WebhookNotificationChannel(
            options,
            new FakeHttpClientFactory(),
            NullLogger<WebhookNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);
    }

    [Fact]
    public void SmtpChannel_NotConfigured_WhenDisabled()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = false,
            Smtp = new SmtpOptions
            {
                Host = "smtp.example.com",
                FromAddress = "test@example.com",
                Recipients = ["recipient@example.com"]
            }
        });

        var channel = new SmtpNotificationChannel(
            options,
            NullLogger<SmtpNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);
    }

    [Fact]
    public void SmtpChannel_NotConfigured_WhenMissingHost()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = true,
            Smtp = new SmtpOptions
            {
                Host = null,
                FromAddress = "test@example.com",
                Recipients = ["recipient@example.com"]
            }
        });

        var channel = new SmtpNotificationChannel(
            options,
            NullLogger<SmtpNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);
    }

    [Fact]
    public void SmtpChannel_NotConfigured_WhenNoRecipients()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = true,
            Smtp = new SmtpOptions
            {
                Host = "smtp.example.com",
                FromAddress = "test@example.com",
                Recipients = []
            }
        });

        var channel = new SmtpNotificationChannel(
            options,
            NullLogger<SmtpNotificationChannel>.Instance);

        Assert.False(channel.IsConfigured);
    }

    [Fact]
    public async Task WebhookChannel_DryRun_DoesNotSend()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = true,
            DryRun = true,
            Webhook = new WebhookOptions { Url = "https://example.com/hook" }
        });

        var factory = new FakeHttpClientFactory();
        var channel = new WebhookNotificationChannel(
            options,
            factory,
            NullLogger<WebhookNotificationChannel>.Instance);

        var result = await channel.SendAsync(CreateTestMessage());

        Assert.Equal(NotificationStatus.DryRun, result.Status);
        Assert.Equal(0, factory.RequestCount); // No HTTP request made
    }

    [Fact]
    public async Task SmtpChannel_DryRun_DoesNotSend()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = true,
            DryRun = true,
            Smtp = new SmtpOptions
            {
                Host = "smtp.example.com",
                FromAddress = "test@example.com",
                Recipients = ["recipient@example.com"]
            }
        });

        var channel = new SmtpNotificationChannel(
            options,
            NullLogger<SmtpNotificationChannel>.Instance);

        var result = await channel.SendAsync(CreateTestMessage());

        // In DryRun mode, returns DryRun status without attempting SMTP connection
        Assert.Equal(NotificationStatus.DryRun, result.Status);
    }

    [Fact]
    public async Task WebhookChannel_RejectsSsrfUrl()
    {
        var options = Options.Create(new NotificationOptions
        {
            Enabled = true,
            DryRun = false,
            Webhook = new WebhookOptions { Url = "https://192.168.1.1/hook" }
        });

        var channel = new WebhookNotificationChannel(
            options,
            new FakeHttpClientFactory(),
            NullLogger<WebhookNotificationChannel>.Instance);

        var result = await channel.SendAsync(CreateTestMessage());

        Assert.Equal(NotificationStatus.Failed, result.Status);
        Assert.Equal("ssrf_blocked", result.FailureCode);
    }

    private static NotificationMessage CreateTestMessage() =>
        new()
        {
            Subject = "Test notification",
            BodyText = "This is a test notification body.",
            Items = [],
            GeneratedAt = DateTimeOffset.UtcNow
        };

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        public int RequestCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            RequestCount++;
            return new HttpClient();
        }
    }
}
