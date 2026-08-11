namespace MarketIntelligence.Agent.Application.Bidding;

/// <summary>
/// Supplies the scheduled collection plans the worker should evaluate. Kept as a
/// port so plans can come from configuration, a file, or a store without the
/// coordinator knowing which.
/// </summary>
public interface IScheduledCollectionPlanSource
{
    Task<IReadOnlyList<ScheduledCollectionPlan>> GetPlansAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fixed in-memory plan source. Default construction yields no plans, so a
/// deployment that has configured nothing schedules nothing.
/// </summary>
public sealed class InMemoryScheduledCollectionPlanSource : IScheduledCollectionPlanSource
{
    private readonly IReadOnlyList<ScheduledCollectionPlan> _plans;

    public InMemoryScheduledCollectionPlanSource(IEnumerable<ScheduledCollectionPlan>? plans = null) =>
        _plans = plans?.ToArray() ?? [];

    public Task<IReadOnlyList<ScheduledCollectionPlan>> GetPlansAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_plans);
}
