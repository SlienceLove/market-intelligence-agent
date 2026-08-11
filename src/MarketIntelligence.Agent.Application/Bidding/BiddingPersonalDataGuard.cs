using System.Text.RegularExpressions;

namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Rejects notices whose retained free text or URL carries personal data that a
/// parser lifted out of a notice body.
/// </summary>
/// <remarks>
/// <para>
/// Detects only what is mechanically separable from organisational text: mainland
/// mobile numbers, landline numbers with a separator, e-mail addresses, and
/// resident ID numbers. Personal names are not detectable this way and are not
/// attempted; see the remarks on <see cref="BiddingNotice"/> for that gap.
/// </para>
/// <para>
/// The guard rejects rather than redacts. A notice carrying a phone number means
/// the parser is pulling from the wrong region of the page, and silently scrubbing
/// the value would hide that defect while the rest of the extraction stays wrong.
/// </para>
/// </remarks>
public static class BiddingPersonalDataGuard
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Mainland mobile number, optionally with a +86 country prefix.</summary>
    private static readonly Regex MobileNumber = new(
        @"(?<!\d)(?:\+?86[-\s]?)?1[3-9]\d{9}(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>
    /// Landline with an explicit separator. A separator is required so that
    /// project codes and budget figures are not mistaken for phone numbers.
    /// </summary>
    private static readonly Regex LandlineNumber = new(
        @"(?<!\d)0\d{2,3}[-\s]\d{7,8}(?:[-\s]\d{1,5})?(?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    private static readonly Regex EmailAddress = new(
        @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    /// <summary>Resident ID number: 17 digits plus a checksum character.</summary>
    private static readonly Regex ResidentIdNumber = new(
        @"(?<!\d)[1-9]\d{5}(?:19|20)\d{2}(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])\d{3}[\dXx](?!\d)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        MatchTimeout);

    public static bool ContainsPersonalData(params string?[] values)
    {
        if (values is null)
        {
            return false;
        }

        foreach (var value in values)
        {
            if (ContainsPersonalData(value))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsPersonalData(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            return ResidentIdNumber.IsMatch(value) ||
                MobileNumber.IsMatch(value) ||
                LandlineNumber.IsMatch(value) ||
                EmailAddress.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological input cannot be cleared, so treat it as suspect.
            return true;
        }
    }
}
