using System.Collections.Concurrent;
using ReachCommander.Application.BatchRenames;
using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed record PlannedRename(
    string OldLogicalPath,
    string NewLogicalPath,
    string OldPhysicalPath,
    string NewPhysicalPath,
    string OldName,
    string NewName,
    FileEntryType Type,
    EntryFingerprint PreviewFingerprint,
    BatchRenamePreviewStatus Status,
    string? Message);

internal sealed record StoredBatchRenamePlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    string DirectoryLogicalPath,
    string DirectoryPhysicalPath,
    IReadOnlyList<PlannedRename> Entries,
    BatchRenamePreview Preview);

internal sealed class BatchRenamePlanStore(TimeProvider clock)
{
    private const int MaximumPlans = 256;
    private readonly ConcurrentDictionary<Guid, StoredBatchRenamePlan> _plans = new();
    private readonly object _gate = new();

    public void AddPlan(StoredBatchRenamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            RemoveExpiredPlans(clock.GetUtcNow());
            _plans[plan.PlanId] = plan;
            while (_plans.Count > MaximumPlans)
            {
                var oldest = _plans.Values.MinBy(candidate => candidate.CreatedAt);
                if (oldest is null || !_plans.TryRemove(oldest.PlanId, out _))
                {
                    break;
                }
            }
        }
    }

    public StoredBatchRenamePlan GetRequiredPlan(Guid planId)
    {
        if (!_plans.TryGetValue(planId, out var plan))
        {
            throw new RenamePlanNotFoundException("The rename preview was not found.");
        }

        if (plan.ExpiresAt <= clock.GetUtcNow())
        {
            _plans.TryRemove(planId, out _);
            throw new RenamePlanExpiredException("The rename preview has expired.");
        }

        return plan;
    }

    private void RemoveExpiredPlans(DateTimeOffset now)
    {
        foreach (var plan in _plans.Values)
        {
            if (plan.ExpiresAt <= now)
            {
                _plans.TryRemove(plan.PlanId, out _);
            }
        }
    }
}
