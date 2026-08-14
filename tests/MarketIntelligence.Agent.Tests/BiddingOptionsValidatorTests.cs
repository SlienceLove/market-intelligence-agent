using MarketIntelligence.Agent.Infrastructure.Bidding;
using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Tests;

public sealed class BiddingOptionsValidatorTests
{
    private readonly BiddingOptionsValidator _validator = new();

    [Fact]
    public void Default_options_are_valid_and_do_not_enable_live_collection()
    {
        var result = _validator.Validate(null, new BiddingOptions());

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("unknown.example")]
    [InlineData("")]
    [InlineData(" jszbtb.com ")]
    public void Unknown_or_malformed_platform_is_rejected(string platform)
    {
        var result = _validator.Validate(null, new BiddingOptions
        {
            Collector = new CollectorSettings { EnabledPlatforms = [platform] }
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("unsupported platform"));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-1, 5)]
    [InlineData(1, 0)]
    [InlineData(1, 6)]
    public void Unsafe_rate_limits_are_rejected(int intervalSeconds, int qps)
    {
        var result = _validator.Validate(null, new BiddingOptions
        {
            Collector = new CollectorSettings
            {
                MinimumIntervalSeconds = intervalSeconds,
                GlobalQpsLimit = qps
            }
        });

        Assert.True(result.Failed);
    }

    [Fact]
    public void Live_platform_requires_absolute_ledger_and_plan_roots()
    {
        var result = _validator.Validate(null, new BiddingOptions
        {
            LedgerRoot = "relative-ledger",
            PlanRoot = null,
            Collector = new CollectorSettings { EnabledPlatforms = ["jszbtb.com"] }
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("LedgerRoot"));
        Assert.Contains(result.Failures!, failure => failure.Contains("PlanRoot"));
    }

    [Fact]
    public void Supported_live_platform_with_absolute_roots_is_valid()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mia-options"));
        var result = _validator.Validate(null, new BiddingOptions
        {
            LedgerRoot = Path.Combine(root, "ledger"),
            PlanRoot = Path.Combine(root, "plans"),
            Collector = new CollectorSettings
            {
                EnabledPlatforms = ["GGZY.GOV.CN"],
                MinimumIntervalSeconds = 1,
                GlobalQpsLimit = 5
            }
        });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Duplicate_platform_ids_are_rejected_case_insensitively()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var result = _validator.Validate(null, new BiddingOptions
        {
            LedgerRoot = root,
            PlanRoot = root,
            Collector = new CollectorSettings
            {
                EnabledPlatforms = ["ggzy.gov.cn", "GGZY.GOV.CN"]
            }
        });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("duplicate platform"));
    }
}
