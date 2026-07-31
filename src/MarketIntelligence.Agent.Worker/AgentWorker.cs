namespace MarketIntelligence.Agent.Worker;

public sealed class AgentWorker(ILogger<AgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent worker started.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
