using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Tests;

public sealed class BiddingNoticeLedgerTests
{
    [Fact]
    public async Task Duplicate_fingerprints_are_suppressed()
    {
        var ledger = new InMemoryNoticeLedger();

        Assert.True(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:abc123"));
        Assert.False(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:abc123"));
    }

    [Fact]
    public async Task Marking_notified_is_idempotent()
    {
        var ledger = new InMemoryNoticeLedger();
        const string fingerprint = "bidding:ccgp.gov.cn:def456";

        await ledger.TryRegisterAsync(fingerprint);
        await ledger.MarkNotifiedAsync(fingerprint);
        await ledger.MarkNotifiedAsync(fingerprint);

        // No exception thrown, and the ledger still suppresses the fingerprint.
        Assert.False(await ledger.TryRegisterAsync(fingerprint));
    }

    [Fact]
    public async Task Marking_unregistered_fingerprint_as_notified_is_a_no_op()
    {
        var ledger = new InMemoryNoticeLedger();

        await ledger.MarkNotifiedAsync("bidding:ccgp.gov.cn:never-registered");

        // The mark call does not implicitly register.
        Assert.True(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:never-registered"));
    }

    [Fact]
    public async Task Pruning_removes_entries_beyond_retention_window()
    {
        var clock = new TestClock();
        var ledger = new InMemoryNoticeLedger(() => clock.Now);

        var t0 = clock.Now;
        await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:old");
        clock.Now = clock.Now.AddMinutes(10);
        var t10 = clock.Now;
        await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:recent");
        clock.Now = clock.Now.AddMinutes(2);
        var t12 = clock.Now;

        var pruned = await ledger.PruneAsync(TimeSpan.FromMinutes(5));
        var cutoff = t12 - TimeSpan.FromMinutes(5);

        Assert.True(t0 < cutoff, $"t0 {t0} should be < cutoff {cutoff}");
        Assert.False(t10 < cutoff, $"t10 {t10} should be >= cutoff {cutoff}");
        Assert.Equal(1, pruned);

        Assert.Equal(1, pruned);
        Assert.True(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:old"));
        Assert.False(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:recent"));
    }

    [Fact]
    public async Task Notified_timestamp_extends_retention()
    {
        var clock = new TestClock();
        var ledger = new InMemoryNoticeLedger(() => clock.Now);

        await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:refreshed");
        clock.Now = clock.Now.AddMinutes(10);
        await ledger.MarkNotifiedAsync("bidding:ccgp.gov.cn:refreshed");
        clock.Now = clock.Now.AddMinutes(3);

        var pruned = await ledger.PruneAsync(TimeSpan.FromMinutes(5));

        // The entry was registered 13 minutes ago but notified 3 minutes ago, so it survives.
        Assert.Equal(0, pruned);
        Assert.False(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:refreshed"));
    }

    [Fact]
    public async Task Rejects_null_or_blank_fingerprints()
    {
        var ledger = new InMemoryNoticeLedger();

        // Null throws ArgumentNullException, which derives from ArgumentException;
        // blank throws ArgumentException directly. Both are rejections.
        await Assert.ThrowsAsync<ArgumentNullException>(() => ledger.TryRegisterAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.TryRegisterAsync("  "));
        await Assert.ThrowsAsync<ArgumentNullException>(() => ledger.MarkNotifiedAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(() => ledger.MarkNotifiedAsync(""));
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    }
}
