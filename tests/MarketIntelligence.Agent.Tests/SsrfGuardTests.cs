using MarketIntelligence.Agent.Infrastructure.Notifications;

namespace MarketIntelligence.Agent.Tests;

public sealed class SsrfGuardTests
{
    [Theory]
    [InlineData("https://example.com/hook")]
    [InlineData("https://api.dingtalk.com/robot/send")]
    [InlineData("https://qyapi.weixin.qq.com/cgi-bin/webhook/send")]
    public void IsWebhookUrlSafe_AcceptsValidHttpsUrls(string url)
    {
        Assert.True(SsrfGuard.IsWebhookUrlSafe(url));
    }

    [Theory]
    [InlineData("http://example.com/hook")] // Not HTTPS
    [InlineData("https://192.168.1.1/hook")] // Private IPv4
    [InlineData("https://10.0.0.1/hook")] // Private IPv4
    [InlineData("https://172.16.0.1/hook")] // Private IPv4
    [InlineData("https://169.254.1.1/hook")] // Link-local IPv4
    [InlineData("https://127.0.0.1/hook")] // Loopback
    [InlineData("https://[::1]/hook")] // Loopback IPv6
    [InlineData("https://[fc00::1]/hook")] // Unique local IPv6
    [InlineData("https://[fe80::1]/hook")] // Link-local IPv6
    public void IsWebhookUrlSafe_RejectsUnsafeUrls(string url)
    {
        Assert.False(SsrfGuard.IsWebhookUrlSafe(url));
    }

    [Fact]
    public void IsWebhookUrlSafe_RejectsNullOrEmpty()
    {
        Assert.False(SsrfGuard.IsWebhookUrlSafe(null));
        Assert.False(SsrfGuard.IsWebhookUrlSafe(string.Empty));
        Assert.False(SsrfGuard.IsWebhookUrlSafe("   "));
    }

    [Fact]
    public void IsWebhookUrlSafe_RejectsInvalidUri()
    {
        Assert.False(SsrfGuard.IsWebhookUrlSafe("not-a-url"));
        Assert.False(SsrfGuard.IsWebhookUrlSafe("htp://malformed"));
    }

    [Fact]
    public async Task IsEmailRecipientSafe_AcceptsValidPublicDomain()
    {
        // gmail.com resolves to public IPs
        var result = await SsrfGuard.IsEmailRecipientSafeAsync("user@gmail.com");
        Assert.True(result);
    }

    [Fact]
    public async Task IsEmailRecipientSafe_RejectsInvalidEmailFormat()
    {
        var result = await SsrfGuard.IsEmailRecipientSafeAsync("not-an-email");
        Assert.False(result);
    }

    [Fact]
    public async Task IsEmailRecipientSafe_RejectsNullOrEmpty()
    {
        Assert.False(await SsrfGuard.IsEmailRecipientSafeAsync(null!));
        Assert.False(await SsrfGuard.IsEmailRecipientSafeAsync(string.Empty));
        Assert.False(await SsrfGuard.IsEmailRecipientSafeAsync("   "));
    }
}
