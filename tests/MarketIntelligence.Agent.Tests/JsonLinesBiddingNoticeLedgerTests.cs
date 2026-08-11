using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class JsonLinesBiddingNoticeLedgerTests : IDisposable
{
    private readonly string _tempDir;

    public JsonLinesBiddingNoticeLedgerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ledger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // Best effort cleanup; don't fail the test if temp cleanup fails.
            }
        }
    }

    [Fact]
    public async Task Ledger_survives_restart()
    {
        var options = Options.Create(new BiddingOptions { LedgerRoot = _tempDir });

        using (var ledger = new JsonLinesBiddingNoticeLedger(options, NullLogger<JsonLinesBiddingNoticeLedger>.Instance))
        {
            Assert.True(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:persistent"));
        }

        using (var reloaded = new JsonLinesBiddingNoticeLedger(options, NullLogger<JsonLinesBiddingNoticeLedger>.Instance))
        {
            Assert.False(await reloaded.TryRegisterAsync("bidding:ccgp.gov.cn:persistent"));
        }
    }

    [Fact]
    public async Task Corrupted_ledger_is_isolated_and_does_not_block_startup()
    {
        var ledgerPath = Path.Combine(_tempDir, "bidding-notices.jsonl");
        await File.WriteAllTextAsync(ledgerPath, "{this is not valid JSON\n");

        var options = Options.Create(new BiddingOptions { LedgerRoot = _tempDir });

        using var ledger = new JsonLinesBiddingNoticeLedger(options, NullLogger<JsonLinesBiddingNoticeLedger>.Instance);

        Assert.True(await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:fresh-after-corruption"));
        Assert.True(Directory.GetFiles(_tempDir, "*.corrupted*").Length > 0);
    }

    [Fact]
    public void Throws_when_ledger_root_is_not_configured()
    {
        var options = Options.Create(new BiddingOptions { LedgerRoot = null });

        Assert.Throws<InvalidOperationException>(() =>
            new JsonLinesBiddingNoticeLedger(options, NullLogger<JsonLinesBiddingNoticeLedger>.Instance));
    }

    [Fact]
    public async Task Prune_compacts_the_file()
    {
        var options = Options.Create(new BiddingOptions { LedgerRoot = _tempDir });

        using (var ledger = new JsonLinesBiddingNoticeLedger(options, NullLogger<JsonLinesBiddingNoticeLedger>.Instance))
        {
            await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:entry1");
            await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:entry2");
            await ledger.TryRegisterAsync("bidding:ccgp.gov.cn:entry3");
        }

        var ledgerPath = Path.Combine(_tempDir, "bidding-notices.jsonl");
        var linesBefore = File.ReadAllLines(ledgerPath).Length;

        using (var ledger = new JsonLinesBiddingNoticeLedger(options, NullLogger<JsonLinesBiddingNoticeLedger>.Instance))
        {
            await Task.Delay(50);
            await ledger.PruneAsync(TimeSpan.FromMilliseconds(10));
        }

        var linesAfter = File.ReadAllLines(ledgerPath).Where(line => !string.IsNullOrWhiteSpace(line)).Count();

        Assert.Equal(3, linesBefore);
        Assert.Equal(0, linesAfter);
    }
}
