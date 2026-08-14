using Microsoft.Extensions.Options;

namespace MarketIntelligence.Agent.Infrastructure.Bidding;

/// <summary>
/// Rejects unsafe or ineffective live-collection settings during host startup.
/// The default configuration remains valid and performs no real collection.
/// </summary>
public sealed class BiddingOptionsValidator : IValidateOptions<BiddingOptions>
{
    private static readonly HashSet<string> SupportedPlatforms = new(
        ["jszbtb.com", "ggzy.gov.cn", "ccgp-jiangsu.gov.cn"],
        StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? name, BiddingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var collector = options.Collector;
        if (collector is null)
        {
            return ValidateOptionsResult.Fail("Bidding:Collector must be configured as an object.");
        }

        if (collector.MinimumIntervalSeconds < 1)
        {
            failures.Add("Bidding:Collector:MinimumIntervalSeconds must be at least 1.");
        }

        if (collector.GlobalQpsLimit is < 1 or > 5)
        {
            failures.Add("Bidding:Collector:GlobalQpsLimit must be between 1 and 5.");
        }

        var enabledPlatforms = collector.EnabledPlatforms;
        if (enabledPlatforms is null)
        {
            failures.Add("Bidding:Collector:EnabledPlatforms cannot be null.");
        }
        else
        {
            var uniquePlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var platform in enabledPlatforms)
            {
                if (string.IsNullOrWhiteSpace(platform) || !SupportedPlatforms.Contains(platform))
                {
                    failures.Add(
                        $"Bidding:Collector:EnabledPlatforms contains unsupported platform '{platform ?? "<null>"}'.");
                    continue;
                }

                if (!uniquePlatforms.Add(platform))
                {
                    failures.Add(
                        $"Bidding:Collector:EnabledPlatforms contains duplicate platform '{platform}'.");
                }
            }

            if (enabledPlatforms.Count > 0)
            {
                ValidateAbsoluteRoot(options.LedgerRoot, "LedgerRoot", failures);
                ValidateAbsoluteRoot(options.PlanRoot, "PlanRoot", failures);
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateAbsoluteRoot(
        string? value,
        string propertyName,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            failures.Add(
                $"Bidding:{propertyName} must be an absolute path when real platforms are enabled.");
        }
    }
}
