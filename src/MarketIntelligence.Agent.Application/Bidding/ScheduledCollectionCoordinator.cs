using MarketIntelligence.Agent.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Default implementation of IScheduledCollectionCoordinator orchestrating
/// collection, deduplication, rendering, and notification delivery.
/// </summary>
public sealed class ScheduledCollectionCoordinator : IScheduledCollectionCoordinator
{
    private readonly IBiddingNoticeCollector _collector;
    private readonly IBiddingNoticeLedger _ledger;
    private readonly INotificationChannel _smtpChannel;
    private readonly INotificationChannel _webhookChannel;
    private readonly IScheduledCollectionHistory _history;
    private readonly IScheduledCollectionPlanSource _planSource;
    private readonly ILogger<ScheduledCollectionCoordinator> _logger;

    public ScheduledCollectionCoordinator(
        IBiddingNoticeCollector collector,
        IBiddingNoticeLedger ledger,
        INotificationChannel smtpChannel,
        INotificationChannel webhookChannel,
        IScheduledCollectionHistory history,
        IScheduledCollectionPlanSource planSource,
        ILogger<ScheduledCollectionCoordinator> logger)
    {
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _smtpChannel = smtpChannel ?? throw new ArgumentNullException(nameof(smtpChannel));
        _webhookChannel = webhookChannel ?? throw new ArgumentNullException(nameof(webhookChannel));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _planSource = planSource ?? throw new ArgumentNullException(nameof(planSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ScheduledCollectionResult> ExecuteAsync(
        ScheduledCollectionPlan plan,
        DateOnly executionDate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.Enabled)
        {
            _logger.LogInformation("Plan {PlanId} is disabled. Skipping execution.", plan.PlanId);
            return ScheduledCollectionResult.Skipped(plan.PlanId, executionDate, "plan_disabled");
        }

        // A malformed plan is rejected before anything is recorded or collected, so
        // a bad configuration cannot burn the day's idempotency slot.
        var planFailure = plan.Validate();
        if (planFailure is not null)
        {
            _logger.LogWarning("Plan {PlanId} is invalid: {FailureCode}", plan.PlanId, planFailure);
            return ScheduledCollectionResult.Failed(plan.PlanId, executionDate, planFailure);
        }

        // Check idempotency: has this (planId, executionDate) already run?
        var existingResult = await _history.GetExecutionResultAsync(plan.PlanId, executionDate, cancellationToken)
            .ConfigureAwait(false);

        if (existingResult is not null && existingResult.Status == ScheduledCollectionStatus.Completed)
        {
            _logger.LogInformation(
                "Plan {PlanId} for {ExecutionDate} already completed. Returning cached result.",
                plan.PlanId, executionDate);
            return existingResult with { WasAlreadyCompleted = true };
        }

        // Record this execution attempt
        var recorded = await _history.TryRecordExecutionAsync(plan.PlanId, executionDate, cancellationToken)
            .ConfigureAwait(false);

        if (!recorded)
        {
            _logger.LogWarning(
                "Plan {PlanId} for {ExecutionDate} is already running or completed.",
                plan.PlanId, executionDate);
            return ScheduledCollectionResult.Skipped(plan.PlanId, executionDate, "already_running");
        }

        try
        {
            // Step 1: Collect
            var request = BuildCollectionRequest(plan, executionDate);
            var collectionResult = await _collector.CollectAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!collectionResult.Succeeded)
            {
                _logger.LogWarning(
                    "Collection failed for plan {PlanId}: {FailureCode}",
                    plan.PlanId, collectionResult.FailureCode);
                var failure = ScheduledCollectionResult.Failed(plan.PlanId, executionDate, collectionResult.FailureCode ?? "unknown");
                await _history.SaveExecutionResultAsync(failure, cancellationToken).ConfigureAwait(false);
                return failure;
            }

            _logger.LogInformation(
                "Plan {PlanId} collected {Count} notices.",
                plan.PlanId, collectionResult.Notices.Count);

            // Step 2: Deduplicate
            var newNotices = new List<BiddingNotice>();
            foreach (var notice in collectionResult.Notices)
            {
                var isNew = await _ledger.TryRegisterAsync(notice.Fingerprint, cancellationToken).ConfigureAwait(false);
                if (isNew)
                {
                    newNotices.Add(notice);
                }
            }

            _logger.LogInformation(
                "Plan {PlanId}: {NewCount} new notices after deduplication (from {TotalCount}).",
                plan.PlanId, newNotices.Count, collectionResult.Notices.Count);

            // Step 3: If no new notices, skip notification
            if (newNotices.Count == 0)
            {
                var skipped = ScheduledCollectionResult.Success(
                    plan.PlanId, executionDate, collectionResult.Notices.Count, 0, 0);
                await _history.SaveExecutionResultAsync(skipped, cancellationToken).ConfigureAwait(false);
                return skipped;
            }

            // Step 4: Render notification
            var message = RenderNotification(plan, newNotices);

            // Step 5: Deliver
            var channel = SelectChannel(plan.NotificationChannel);
            if (!channel.IsConfigured)
            {
                _logger.LogWarning("Notification channel {Channel} is not configured for plan {PlanId}.",
                    plan.NotificationChannel, plan.PlanId);
                var failure = ScheduledCollectionResult.Failed(plan.PlanId, executionDate, "notification_not_configured");
                await _history.SaveExecutionResultAsync(failure, cancellationToken).ConfigureAwait(false);
                return failure;
            }

            var notificationResult = await channel.SendAsync(message, cancellationToken).ConfigureAwait(false);

            if (!notificationResult.Succeeded)
            {
                _logger.LogWarning(
                    "Notification delivery failed for plan {PlanId}: {FailureCode}",
                    plan.PlanId, notificationResult.FailureCode);
                var failure = ScheduledCollectionResult.Failed(plan.PlanId, executionDate, notificationResult.FailureCode ?? "notification_failed");
                await _history.SaveExecutionResultAsync(failure, cancellationToken).ConfigureAwait(false);
                return failure;
            }

            // Step 6: Mark as notified
            foreach (var notice in newNotices)
            {
                await _ledger.MarkNotifiedAsync(notice.Fingerprint, cancellationToken).ConfigureAwait(false);
            }

            var success = ScheduledCollectionResult.Success(
                plan.PlanId,
                executionDate,
                collectionResult.Notices.Count,
                newNotices.Count,
                newNotices.Count,
                notificationResult.NotificationId);

            await _history.SaveExecutionResultAsync(success, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Plan {PlanId} completed successfully. Notification {NotificationId} delivered.",
                plan.PlanId, notificationResult.NotificationId);

            return success;
        }
        catch (OperationCanceledException)
        {
            // Worker shutdown. The slot is released as Skipped rather than Failed so
            // the next start can re-run it, and CancellationToken.None is used for
            // the write because the ambient token is already cancelled.
            _logger.LogInformation("Plan {PlanId} was cancelled mid-run.", plan.PlanId);
            var cancelled = ScheduledCollectionResult.Skipped(plan.PlanId, executionDate, "cancelled");
            await _history.SaveExecutionResultAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unexpected error executing plan {PlanId}.", plan.PlanId);
            var failure = ScheduledCollectionResult.Failed(plan.PlanId, executionDate, "internal_error");
            await _history.SaveExecutionResultAsync(failure, cancellationToken).ConfigureAwait(false);
            return failure;
        }
    }

    public async Task<IReadOnlyList<ScheduledCollectionResult>> ExecuteDuePlansAsync(
        DateTimeOffset currentTime,
        CancellationToken cancellationToken = default)
    {
        var plans = await _planSource.GetPlansAsync(cancellationToken).ConfigureAwait(false);
        var executionDate = DateOnly.FromDateTime(currentTime.UtcDateTime);
        var results = new List<ScheduledCollectionResult>();

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!plan.IsDueAt(currentTime))
            {
                continue;
            }

            // One plan's failure must not stop the remaining plans. ExecuteAsync
            // already converts expected failures into results; this guard covers a
            // plan source handing back something pathological.
            try
            {
                results.Add(await ExecuteAsync(plan, executionDate, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Plan {PlanId} threw during scheduled execution.", plan.PlanId);
                results.Add(ScheduledCollectionResult.Failed(plan.PlanId, executionDate, "internal_error"));
            }
        }

        return results;
    }

    /// <summary>
    /// Builds the collection request for a plan run. The collection id is derived
    /// from (planId, executionDate) so a retried run reuses the same id and stays
    /// traceable to one scheduled slot. Both window ends are supplied because the
    /// request contract rejects a half-open window.
    /// </summary>
    private static BiddingCollectionRequest BuildCollectionRequest(ScheduledCollectionPlan plan, DateOnly executionDate)
    {
        // Use end-of-day so notices published on executionDate itself are included.
        var toDate = new DateTimeOffset(executionDate.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);
        var fromDate = toDate.AddDays(-plan.LookbackDays);

        return new BiddingCollectionRequest
        {
            CollectionId = BuildCollectionId(plan.PlanId, executionDate),
            Keywords = plan.Keywords.ToArray(),
            Region = plan.RegionFilter,
            Industry = plan.IndustryFilter,
            FromDate = fromDate,
            ToDate = toDate,
            MaxResults = plan.MaxResults
        };
    }

    private static string BuildCollectionId(string planId, DateOnly executionDate)
    {
        var sanitizedPlanId = new string(planId
            .Where(character => !char.IsControl(character) && !char.IsWhiteSpace(character))
            .ToArray());

        var candidate = $"sched-{sanitizedPlanId}-{executionDate:yyyyMMdd}";
        return candidate.Length > BiddingContractLimits.MaxCollectionIdCharacters
            ? candidate[..BiddingContractLimits.MaxCollectionIdCharacters]
            : candidate;
    }

    private static NotificationMessage RenderNotification(ScheduledCollectionPlan plan, List<BiddingNotice> notices)
    {
        var bodyText = $"发现 {notices.Count} 条新招投标公告：\n\n" +
            string.Join("\n", notices.Take(20).Select(n => $"- {n.Title} ({n.Publisher}, {n.PublishedAt:yyyy-MM-dd})"));

        var bodyMarkdown = $"## {plan.Name}\n\n发现 **{notices.Count}** 条新招投标公告：\n\n" +
            string.Join("\n", notices.Take(20).Select(n => $"- [{n.Title}]({n.NoticeUrl}) — {n.Publisher}, {n.PublishedAt:yyyy-MM-dd}"));

        var items = notices.Take(20).Select(n => new NotificationItem
        {
            Title = n.Title,
            Url = n.NoticeUrl,
            Publisher = n.Publisher,
            PublishedAt = n.PublishedAt,
            Region = n.Region,
            AmountRange = n.AmountRange
        }).ToArray();

        return new NotificationMessage
        {
            Subject = $"{plan.Name} — {notices.Count} 条新公告",
            BodyText = bodyText,
            BodyMarkdown = bodyMarkdown,
            Items = items,
            GeneratedAt = DateTimeOffset.UtcNow
        };
    }

    private INotificationChannel SelectChannel(string channelName) =>
        channelName.Trim().Equals("smtp", StringComparison.OrdinalIgnoreCase) ? _smtpChannel :
        channelName.Trim().Equals("webhook", StringComparison.OrdinalIgnoreCase) ? _webhookChannel :
        throw new InvalidOperationException($"Unknown notification channel: {channelName}");
}
