using System.Collections.Concurrent;

namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal sealed class InMemoryFileOperationPlanStore(TimeProvider clock) : IFileOperationPlanStore
{
    private const int MaximumPlans = 256;
    private readonly ConcurrentDictionary<Guid, FileOperationPlan> _plans = new();

    public ValueTask SaveAsync(FileOperationPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExpired();
        _plans[plan.PlanId] = plan;
        while (_plans.Count > MaximumPlans)
        {
            var oldest = _plans.Values.MinBy(candidate => candidate.CreatedAt);
            if (oldest is null || !_plans.TryRemove(oldest.PlanId, out _))
            {
                break;
            }
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<FileOperationPlan?> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_plans.GetValueOrDefault(planId));
    }

    public ValueTask DeleteAsync(Guid planId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _plans.TryRemove(planId, out _);
        return ValueTask.CompletedTask;
    }

    private void RemoveExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var plan in _plans.Values.Where(candidate => candidate.ExpiresAt <= now))
        {
            _plans.TryRemove(plan.PlanId, out _);
        }
    }
}
