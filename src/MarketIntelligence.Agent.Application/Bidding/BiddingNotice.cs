namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// A single public bidding notice, reduced to the fields the compliance boundary
/// in docs/ops/bidding-collection-compliance.md permits.
/// </summary>
/// <remarks>
/// <para>
/// The no-PII rule is enforced two ways, and it is worth being precise about
/// which is which. Structurally, there is no contact-name, phone, or e-mail
/// property, so a parser has nowhere to put such a value. Mechanically,
/// <see cref="Validate"/> rejects a notice whose retained free text or URL
/// contains a mainland phone number, an e-mail address, or a resident ID number,
/// because a parser can otherwise smuggle those into <see cref="Title"/> or
/// <see cref="Publisher"/>.
/// </para>
/// <para>
/// What neither mechanism catches is a bare personal name: "张三" and
/// "张三采购中心" are not mechanically separable, so an organisation field holding
/// a natural person's name still depends on parser discipline. That residual gap
/// is real and is not claimed to be closed here.
/// </para>
/// </remarks>
public sealed record BiddingNotice
{
    public required string Title { get; init; }

    /// <summary>Issuing organisation name. Not a person.</summary>
    public required string Publisher { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public string? Region { get; init; }

    public string? Industry { get; init; }

    /// <summary>
    /// Free-form public amount range as published (for example "100-500万元").
    /// Kept as text because platforms publish wildly inconsistent formats and
    /// parsing them into a number would invent precision the source lacks.
    /// </summary>
    public string? AmountRange { get; init; }

    public required string NoticeUrl { get; init; }

    /// <summary>Host of the originating platform, for example "ccgp.gov.cn".</summary>
    public required string SourcePlatform { get; init; }

    /// <summary>Stable dedupe identity; see <see cref="BiddingNoticeFingerprint"/>.</summary>
    public required string Fingerprint { get; init; }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Title) ||
            Title.Length > BiddingContractLimits.MaxTitleCharacters ||
            Title.Any(char.IsControl))
        {
            return "invalid_notice_title";
        }

        if (string.IsNullOrWhiteSpace(Publisher) ||
            Publisher.Length > BiddingContractLimits.MaxPublisherCharacters ||
            Publisher.Any(char.IsControl))
        {
            return "invalid_notice_publisher";
        }

        if (string.IsNullOrWhiteSpace(SourcePlatform) ||
            SourcePlatform.Length > BiddingContractLimits.MaxSourcePlatformCharacters ||
            SourcePlatform.Any(char.IsControl))
        {
            return "invalid_source_platform";
        }

        if (string.IsNullOrWhiteSpace(Fingerprint) ||
            Fingerprint.Length > BiddingContractLimits.MaxFingerprintCharacters ||
            Fingerprint.Any(char.IsControl))
        {
            return "invalid_notice_fingerprint";
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

        if (AmountRange is not null &&
            (AmountRange.Length > BiddingContractLimits.MaxAmountRangeCharacters || AmountRange.Any(char.IsControl)))
        {
            return "invalid_notice";
        }

        var urlFailure = ValidateUrl();
        if (urlFailure is not null)
        {
            return urlFailure;
        }

        return BiddingPersonalDataGuard.ContainsPersonalData(
            Title, Publisher, Region, Industry, AmountRange, NoticeUrl)
            ? "personal_data_detected"
            : null;
    }

    /// <summary>
    /// The notice URL is the one field that later flows into a rendered push
    /// message, so it is constrained to public HTTP(S) locations here rather
    /// than at render time.
    /// </summary>
    private string? ValidateUrl()
    {
        if (string.IsNullOrWhiteSpace(NoticeUrl) ||
            NoticeUrl.Length > BiddingContractLimits.MaxNoticeUrlCharacters ||
            NoticeUrl.Any(char.IsControl))
        {
            return "invalid_notice_url";
        }

        if (!Uri.TryCreate(NoticeUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "invalid_notice_url";
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) || uri.IsLoopback)
        {
            return "unsafe_notice_url";
        }

        return null;
    }
}
