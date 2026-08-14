namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// A scheduled collection plan: what to collect, when to trigger, and where to
/// send the result.
/// </summary>
/// <remarks>
/// The trigger is expressed as a UTC time-of-day plus an optional day-of-week
/// set rather than a cron string. The plan owns <see cref="IsDueAt"/> as a pure
/// function of an injected instant, so scheduling is testable with a fixed clock
/// and carries no dependency on a scheduling library. Cron support, if it is ever
/// needed, becomes an alternative trigger type rather than a rewrite of the
/// coordinator.
/// </remarks>
public sealed record ScheduledCollectionPlan
{
    /// <summary>Identity used together with the execution date for idempotency.</summary>
    public required string PlanId { get; init; }

    /// <summary>Human-readable name; also used as the notification subject prefix.</summary>
    public required string Name { get; init; }

    public required IReadOnlyList<string> Keywords { get; init; }

    /// <summary>Optional region filter, passed through to the collection request.</summary>
    public string? RegionFilter { get; init; }

    /// <summary>Optional industry filter, passed through to the collection request.</summary>
    public string? IndustryFilter { get; init; }

    /// <summary>How many days back the collection window reaches. Defaults to 1.</summary>
    public int LookbackDays { get; init; } = 1;

    public int MaxResults { get; init; } = 50;

    /// <summary>Target channel key: <c>smtp</c> or <c>webhook</c>.</summary>
    public required string NotificationChannel { get; init; }

    /// <summary>UTC time of day at which the plan becomes due.</summary>
    public required TimeOnly ExecutionTimeUtc { get; init; }

    /// <summary>
    /// Days on which the plan may run. Empty means every day. Kept as a set so a
    /// weekday-only plan does not need a cron expression.
    /// </summary>
    public IReadOnlySet<DayOfWeek> DaysOfWeek { get; init; } = new HashSet<DayOfWeek>();

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// True when <paramref name="instant"/> falls on an allowed day and has reached
    /// the plan's execution time. Being past the time still counts as due: a worker
    /// that was down at the exact minute must still run the slot once, and
    /// (planId, date) idempotency is what prevents a second push.
    /// </summary>
    public bool IsDueAt(DateTimeOffset instant)
    {
        if (!Enabled)
        {
            return false;
        }

        var utc = instant.ToUniversalTime();

        if (DaysOfWeek.Count > 0 && !DaysOfWeek.Contains(utc.DayOfWeek))
        {
            return false;
        }

        return TimeOnly.FromDateTime(utc.DateTime) >= ExecutionTimeUtc;
    }

    /// <summary>
    /// Validates plan bounds against the shared collection limits. Returns a
    /// catalog failure code, or null when the plan is well formed.
    /// </summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(PlanId) ||
            PlanId.Length > BiddingContractLimits.MaxCollectionIdCharacters ||
            PlanId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            return "invalid_plan_id";
        }

        if (string.IsNullOrWhiteSpace(Name) ||
            Name.Length > BiddingContractLimits.MaxTitleCharacters ||
            Name.Any(char.IsControl))
        {
            return "invalid_plan_name";
        }

        if (Keywords is null || Keywords.Count == 0)
        {
            return "keyword_required";
        }

        if (Keywords.Count > BiddingContractLimits.MaxKeywords)
        {
            return "keyword_limit_exceeded";
        }

        if (Keywords.Any(keyword =>
                string.IsNullOrWhiteSpace(keyword) ||
                keyword.Length > BiddingContractLimits.MaxKeywordCharacters ||
                keyword.Any(char.IsControl)))
        {
            return "invalid_keyword";
        }

        if (LookbackDays <= 0 || LookbackDays > BiddingContractLimits.MaxTimeWindow.TotalDays)
        {
            return "invalid_time_window";
        }

        if (MaxResults <= 0 || MaxResults > BiddingContractLimits.MaxResultsCeiling)
        {
            return "invalid_max_results";
        }

        if (!string.IsNullOrWhiteSpace(RegionFilter) &&
            (RegionFilter.Length > BiddingContractLimits.MaxRegionCharacters ||
             RegionFilter.Any(char.IsControl)))
        {
            return "invalid_region";
        }

        if (!string.IsNullOrWhiteSpace(IndustryFilter) &&
            (IndustryFilter.Length > BiddingContractLimits.MaxIndustryCharacters ||
             IndustryFilter.Any(char.IsControl)))
        {
            return "invalid_industry";
        }

        return ScheduledNotificationChannels.IsKnown(NotificationChannel)
            ? null
            : "invalid_notification_channel";
    }
}

/// <summary>
/// Channel keys a plan may target. Kept here so the plan can validate its own
/// channel without depending on the notification infrastructure.
/// </summary>
public static class ScheduledNotificationChannels
{
    public const string Smtp = "smtp";
    public const string Webhook = "webhook";

    public static bool IsKnown(string? channel) =>
        !string.IsNullOrWhiteSpace(channel) &&
        (channel.Trim().Equals(Smtp, StringComparison.OrdinalIgnoreCase) ||
         channel.Trim().Equals(Webhook, StringComparison.OrdinalIgnoreCase));
}
