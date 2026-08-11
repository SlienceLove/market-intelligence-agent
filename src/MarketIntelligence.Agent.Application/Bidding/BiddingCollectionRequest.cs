namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// A keyword-driven bidding collection request. All bounds are enforced here so
/// a malformed or oversized request fails before any provider is contacted.
/// </summary>
public sealed record BiddingCollectionRequest
{
    public required string CollectionId { get; init; }

    public required IReadOnlyList<string> Keywords { get; init; }

    public string? Region { get; init; }

    public string? Industry { get; init; }

    public DateTimeOffset? FromDate { get; init; }

    public DateTimeOffset? ToDate { get; init; }

    public int MaxResults { get; init; } = BiddingContractLimits.MaxResultsCeiling;

    /// <summary>Correlation identifier shared with the media job vocabulary.</summary>
    public string? CorrelationId { get; init; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(CollectionId))
        {
            return "collection_id_required";
        }

        if (CollectionId.Length > BiddingContractLimits.MaxCollectionIdCharacters ||
            CollectionId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return "invalid_collection_id";
        }

        var keywordFailure = ValidateKeywords();
        if (keywordFailure is not null)
        {
            return keywordFailure;
        }

        if (Region is not null &&
            (Region.Length > BiddingContractLimits.MaxRegionCharacters || Region.Any(char.IsControl)))
        {
            return "invalid_region";
        }

        if (Industry is not null &&
            (Industry.Length > BiddingContractLimits.MaxIndustryCharacters || Industry.Any(char.IsControl)))
        {
            return "invalid_industry";
        }

        if (MaxResults <= 0 || MaxResults > BiddingContractLimits.MaxResultsCeiling)
        {
            return "invalid_max_results";
        }

        return ValidateTimeWindow();
    }

    private string? ValidateKeywords()
    {
        if (Keywords is null || Keywords.Count == 0)
        {
            return "keyword_required";
        }

        if (Keywords.Count > BiddingContractLimits.MaxKeywords)
        {
            return "keyword_limit_exceeded";
        }

        foreach (var keyword in Keywords)
        {
            // A blank entry inside a supplied list is a malformed entry, not an
            // absent one: keyword_required means "send a keyword", invalid_keyword
            // means "fix the one you sent". Callers act differently on each.
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return "invalid_keyword";
            }

            if (keyword.Length > BiddingContractLimits.MaxKeywordCharacters ||
                keyword.Any(char.IsControl))
            {
                return "invalid_keyword";
            }
        }

        return null;
    }

    private string? ValidateTimeWindow()
    {
        if (FromDate is null && ToDate is null)
        {
            return null;
        }

        if (FromDate is not null && ToDate is not null)
        {
            if (ToDate < FromDate)
            {
                return "invalid_time_window";
            }

            if (ToDate - FromDate > BiddingContractLimits.MaxTimeWindow)
            {
                return "invalid_time_window";
            }
        }

        return null;
    }
}
