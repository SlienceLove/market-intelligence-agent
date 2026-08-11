using System.Collections.ObjectModel;

namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Terminal and in-flight states for a bidding collection run.
/// </summary>
public enum BiddingCollectionStatus
{
    Succeeded = 0,
    Failed = 1,
    Cancelled = 2,
    Running = 3
}

/// <summary>
/// Failure classification for bidding collection, mirroring
/// <see cref="Media.MediaFailureCategory"/> so API status mapping stays uniform.
/// </summary>
public enum BiddingFailureCategory
{
    None,
    Validation,
    Security,
    Authorization,
    LimitExceeded,
    RateLimited,
    Timeout,
    ProviderUnavailable,
    Transient,
    EmptyResult,
    Cancelled,
    Internal,
    Unknown
}

/// <summary>
/// Size and range ceilings enforced by request, notice, and result validation.
/// Centralized so the API, the collectors, and the tests agree on one bound per
/// field instead of each rediscovering its own.
/// </summary>
public static class BiddingContractLimits
{
    public const int MaxCollectionIdCharacters = 128;
    public const int MaxKeywords = 32;
    public const int MaxKeywordCharacters = 128;
    public const int MaxTitleCharacters = 256;
    public const int MaxPublisherCharacters = 128;
    public const int MaxRegionCharacters = 64;
    public const int MaxIndustryCharacters = 64;
    public const int MaxAmountRangeCharacters = 64;
    public const int MaxNoticeUrlCharacters = 2_048;
    public const int MaxSourcePlatformCharacters = 128;
    public const int MaxFingerprintCharacters = 256;
    public const int MaxFailureCodeCharacters = 64;
    public const int MaxFailureMessageCharacters = 512;

    /// <summary>
    /// Upper bound on notices returned by a single run. The compliance boundary
    /// caps collection volume per platform; see docs/ops/bidding-collection-compliance.md.
    /// </summary>
    public const int MaxResultsCeiling = 100;

    /// <summary>Longest time window a single request may span.</summary>
    public static readonly TimeSpan MaxTimeWindow = TimeSpan.FromDays(365);
}

/// <summary>
/// Stable failure-code catalog for the bidding pipeline: classification, retry
/// policy, and message sanitization. Codes are part of the contract, so entries
/// are added rather than renamed once a caller can observe them.
/// </summary>
public static class BiddingFailureCatalog
{
    private static readonly IReadOnlyDictionary<string, BiddingFailureCategory> Categories =
        new ReadOnlyDictionary<string, BiddingFailureCategory>(new Dictionary<string, BiddingFailureCategory>(StringComparer.OrdinalIgnoreCase)
        {
            ["collection_id_required"] = BiddingFailureCategory.Validation,
            ["invalid_collection_id"] = BiddingFailureCategory.Validation,
            ["keyword_required"] = BiddingFailureCategory.Validation,
            ["invalid_keyword"] = BiddingFailureCategory.Validation,
            ["keyword_limit_exceeded"] = BiddingFailureCategory.LimitExceeded,
            ["invalid_time_window"] = BiddingFailureCategory.Validation,
            ["invalid_max_results"] = BiddingFailureCategory.Validation,
            ["invalid_region"] = BiddingFailureCategory.Validation,
            ["invalid_industry"] = BiddingFailureCategory.Validation,
            ["invalid_notice"] = BiddingFailureCategory.Validation,
            ["invalid_notice_url"] = BiddingFailureCategory.Validation,
            ["invalid_notice_title"] = BiddingFailureCategory.Validation,
            ["invalid_notice_publisher"] = BiddingFailureCategory.Validation,
            ["invalid_notice_fingerprint"] = BiddingFailureCategory.Validation,
            ["invalid_source_platform"] = BiddingFailureCategory.Validation,
            ["unsafe_notice_url"] = BiddingFailureCategory.Security,
            ["bidding_source_not_configured"] = BiddingFailureCategory.ProviderUnavailable,
            ["bidding_source_not_allowed"] = BiddingFailureCategory.Authorization,
            ["robots_disallowed"] = BiddingFailureCategory.Authorization,
            ["unauthorized"] = BiddingFailureCategory.Authorization,
            ["forbidden"] = BiddingFailureCategory.Authorization,
            ["notice_parse_failed"] = BiddingFailureCategory.Transient,
            ["notice_limit_exceeded"] = BiddingFailureCategory.LimitExceeded,
            ["rate_limited"] = BiddingFailureCategory.RateLimited,
            ["timeout"] = BiddingFailureCategory.Timeout,
            ["provider_unavailable"] = BiddingFailureCategory.ProviderUnavailable,
            ["transient_provider_failure"] = BiddingFailureCategory.Transient,
            ["empty_collection_result"] = BiddingFailureCategory.EmptyResult,
            ["cancelled"] = BiddingFailureCategory.Cancelled,
            ["invalid_request"] = BiddingFailureCategory.Validation,
            ["invalid_status"] = BiddingFailureCategory.Validation,
            ["failure_code_required"] = BiddingFailureCategory.Validation,
            ["internal_error"] = BiddingFailureCategory.Internal
        });

    /// <summary>
    /// Every explicitly registered code. Exposed so collectors added in later
    /// tasks can be asserted against the catalog instead of silently emitting a
    /// code that falls through to <see cref="BiddingFailureCategory.Unknown"/>.
    /// </summary>
    public static IReadOnlyCollection<string> KnownCodes => (IReadOnlyCollection<string>)Categories.Keys;

    /// <summary>
    /// True when <paramref name="failureCode"/> is registered in the catalog
    /// rather than merely matching a heuristic fallback.
    /// </summary>
    public static bool IsRegistered(string? failureCode) =>
        !string.IsNullOrWhiteSpace(failureCode) && Categories.ContainsKey(failureCode.Trim());

    public static BiddingFailureCategory Classify(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return BiddingFailureCategory.None;
        }

        var normalized = failureCode.Trim();
        if (Categories.TryGetValue(normalized, out var category))
        {
            return category;
        }

        if (normalized.StartsWith("invalid_", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("_required", StringComparison.OrdinalIgnoreCase))
        {
            return BiddingFailureCategory.Validation;
        }

        if (normalized.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return BiddingFailureCategory.Timeout;
        }

        if (normalized.Contains("rate", StringComparison.OrdinalIgnoreCase))
        {
            return BiddingFailureCategory.RateLimited;
        }

        if (normalized.StartsWith("provider_", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("_not_configured", StringComparison.OrdinalIgnoreCase))
        {
            return BiddingFailureCategory.ProviderUnavailable;
        }

        if (normalized.EndsWith("_exceeded", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            return BiddingFailureCategory.LimitExceeded;
        }

        return BiddingFailureCategory.Unknown;
    }

    /// <summary>
    /// A not-configured provider is deliberately non-retryable: retrying cannot
    /// make configuration appear, and retry loops would mask the real cause.
    /// </summary>
    public static bool IsRetryable(string? failureCode)
    {
        if (string.Equals(failureCode?.Trim(), "bidding_source_not_configured", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Classify(failureCode) is BiddingFailureCategory.RateLimited or
            BiddingFailureCategory.Timeout or
            BiddingFailureCategory.ProviderUnavailable or
            BiddingFailureCategory.Transient;
    }

    /// <summary>
    /// Keeps platform messages useful while preventing credentials, URLs, paths,
    /// and control characters from crossing the application boundary.
    /// </summary>
    public static string? SanitizeMessage(string? failureCode, string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var normalized = message.Trim();
        if (normalized.Any(char.IsControl) ||
            normalized.Contains("://", StringComparison.Ordinal) ||
            normalized.Contains("token", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains('\\'))
        {
            return SafeDefaultMessage(failureCode);
        }

        return normalized.Length <= BiddingContractLimits.MaxFailureMessageCharacters
            ? normalized
            : normalized[..BiddingContractLimits.MaxFailureMessageCharacters];
    }

    public static string SafeDefaultMessage(string? failureCode) =>
        "The bidding collection failed.";
}
