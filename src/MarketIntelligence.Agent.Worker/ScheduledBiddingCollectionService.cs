using MarketIntelligence.Agent.Application.Bidding;

namespace MarketIntelligence.Agent.Worker;

/// <summary>
/// Drives scheduled bidding collection: on each tick it asks the coordinator to
/// run whatever plans are due. Due-evaluation and (plan, date) idempotency live in
/// the coordinator, so this service only owns the tick and the shutdown behaviour.
/// </summary>
public sealed class ScheduledBiddingCollectionService(
    IScheduledCollectionCoordinator coordinator,
    ILogger<ScheduledBiddingCollectionService> logger) : BackgroundService
{
    /// <summary>
    /// Tick interval. A plan stays due for the rest of its UTC day, so a one-minute
    /// poll only affects how promptly a slot is picked up, never whether it runs.
    /// </summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Scheduled bidding collection service started. Poll interval: {Interval}.", PollInterval);

        using var timer = new PeriodicTimer(PollInterval);

        // Run once at startup so a restart does not wait a full interval before
        // picking up a slot that is already due.
        await RunDuePlansAsync(stoppingToken).ConfigureAwait(false);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await RunDuePlansAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        logger.LogInformation("Scheduled bidding collection service stopped.");
    }

    private async Task RunDuePlansAsync(CancellationToken cancellationToken)
    {
        try
        {
            var results = await coordinator
                .ExecuteDuePlansAsync(DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);

            if (results.Count == 0)
            {
                return;
            }

            foreach (var result in results)
            {
                if (result.Succeeded)
                {
                    logger.LogInformation(
                        "Plan {PlanId} for {Date}: collected {Collected}, new {New}, notified {Notified}.",
                        result.PlanId, result.ExecutionDate,
                        result.NoticesCollected, result.NoticesDeduplicated, result.NoticesNotified);
                }
                else
                {
                    logger.LogWarning(
                        "Plan {PlanId} for {Date} ended as {Status} ({FailureCode}).",
                        result.PlanId, result.ExecutionDate, result.Status, result.FailureCode);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A tick failure must not tear down the worker: the next tick retries.
            logger.LogError(exception, "Scheduled bidding collection tick failed.");
        }
    }
}
