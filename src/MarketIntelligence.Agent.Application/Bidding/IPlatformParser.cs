namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Parses bidding notices from a platform-specific HTTP response.
/// </summary>
public interface IPlatformParser
{
    /// <summary>
    /// The platform identifier this parser handles (e.g., "chinabidding", "ccgp").
    /// </summary>
    string PlatformId { get; }

    /// <summary>
    /// Builds the platform-specific search URI from the collection request.
    /// </summary>
    Uri BuildSearchUri(BiddingCollectionRequest request);

    /// <summary>
    /// Parses bidding notices from the HTTP response content.
    /// </summary>
    /// <returns>Parsed notices, or empty array if none found.</returns>
    Task<BiddingNotice[]> ParseAsync(
        string content,
        BiddingCollectionRequest request,
        CancellationToken cancellationToken);
}
