using Microsoft.Extensions.Logging;

namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Executes on-demand bidding collection for specified plans or all registered plans.
/// Unlike the scheduled worker, this service ignores IsDueAt and runs every requested
/// plan immediately, relying on the coordinator's (planId, date) idempotency guard.
/// </summary>
public sealed class OnDemandCollectionService
{
    private readonly IScheduledCollectionCoordinator _coordinator;
    private readonly IScheduledCollectionPlanSource _planSource;
    private readonly ILogger<OnDemandCollectionService> _logger;

    public OnDemandCollectionService(
        IScheduledCollectionCoordinator coordinator,
        IScheduledCollectionPlanSource planSource,
        ILogger<OnDemandCollectionService> logger)
    {
        _coordinator = coordinator;
        _planSource = planSource;
        _logger = logger;
    }

    public async Task<CollectOnDemandResponse> ExecuteAsync(
        CollectOnDemandRequest request,
        CancellationToken cancellationToken = default)
    {
        var asOf = request.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var allPlans = await _planSource.GetPlansAsync(cancellationToken);

        List<ScheduledCollectionPlan> plansToRun;
        if (request.PlanIds.Count > 0)
        {
            var planIdSet = new HashSet<string>(request.PlanIds, StringComparer.OrdinalIgnoreCase);
            plansToRun = allPlans.Where(p => planIdSet.Contains(p.PlanId)).ToList();
        }
        else
        {
            plansToRun = allPlans.ToList();
        }

        var summaries = new List<PlanCollectionSummary>(plansToRun.Count);
        var succeededCount = 0;
        var skippedCount = 0;

        foreach (var plan in plansToRun)
        {
            try
            {
                var result = await _coordinator.ExecuteAsync(plan, asOf, cancellationToken);
                var wasSkipped = result.Status == ScheduledCollectionStatus.Skipped ||
                                 result.WasAlreadyCompleted;

                if (result.Succeeded && !wasSkipped)
                {
                    succeededCount++;
                }
                else if (wasSkipped)
                {
                    // A completed result can be returned from the coordinator's cache.
                    // Normalize that response to the on-demand API's explicit skipped
                    // outcome so callers can tell a fresh run from a duplicate request.
                    skippedCount++;
                }

                summaries.Add(new PlanCollectionSummary
                {
                    PlanId = result.PlanId,
                    NoticesCollected = wasSkipped ? 0 : result.NoticesCollected,
                    Outcome = wasSkipped ? "skipped" : result.Status.ToString().ToLowerInvariant(),
                    Error = wasSkipped ? (result.FailureCode ?? "already_completed") : result.FailureCode
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex, "On-demand collection failed unexpectedly for plan {PlanId}.", plan.PlanId);
                summaries.Add(new PlanCollectionSummary
                {
                    PlanId = plan.PlanId,
                    NoticesCollected = 0,
                    Outcome = "failed",
                    Error = "internal_error"
                });
            }
        }

        var nonFailedCount = succeededCount + skippedCount;
        var status = summaries.Count == 0 ? "failed"
            : nonFailedCount == summaries.Count ? "success"
            : nonFailedCount > 0 ? "partial"
            : "failed";

        return new CollectOnDemandResponse
        {
            PlansExecuted = summaries.Count,
            TotalNoticesCollected = summaries.Sum(s => s.NoticesCollected),
            Plans = summaries,
            Status = status,
            SkippedCount = skippedCount
        };
    }
}
