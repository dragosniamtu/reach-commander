using ReachCommander.Application.Archives;
using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.Infrastructure.Archives.Extraction;

internal sealed record PlannedArchiveFile(
    int WorkerEntryIndex,
    string ArchivePath,
    string RelativeOutputPath,
    long? DeclaredSize,
    long? DeclaredCompressedSize,
    DateTimeOffset? ModifiedAt);

internal sealed record ArchiveExtractionPlan(
    string PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string SourceId,
    string ArchivePath,
    ResolvedArchivePartSet PartSet,
    string InternalDirectory,
    IReadOnlyList<string> SelectedRoots,
    IReadOnlyList<PlannedArchiveFile> Files,
    IReadOnlyList<string> Directories,
    string DestinationSourceId,
    string DestinationPath,
    string DestinationSnapshot,
    IReadOnlyList<ArchiveExtractionIssue> Conflicts,
    IReadOnlyList<ArchiveExtractionIssue> Violations,
    bool CanExecute);

internal sealed class ArchiveExtractionPlanStore(
    TimeProvider clock,
    ArchiveExtractionOperationStore operations)
{
    private const int MaximumPlans = 128;
    private readonly Dictionary<string, ArchiveExtractionPlan> _plans =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, OperationBinding> _operationIdsByPlan =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Add(ArchiveExtractionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            RemoveExpired(clock.GetUtcNow());
            if (_plans.ContainsKey(plan.PlanId))
            {
                throw new InvalidOperationException("An archive extraction plan ID was reused.");
            }

            if (_plans.Count >= MaximumPlans)
            {
                var oldest = _plans.Values
                    .Where(existing => !_operationIdsByPlan.ContainsKey(existing.PlanId))
                    .MinBy(existing => existing.CreatedAt);
                if (oldest is null)
                {
                    throw new ArchiveCapacityReachedException();
                }

                _plans.Remove(oldest.PlanId);
            }

            _plans.Add(plan.PlanId, plan);
        }
    }

    public ArchiveExtractionPlan GetRequiredPlan(string planId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        lock (_gate)
        {
            if (!_plans.TryGetValue(planId, out var plan))
            {
                throw new ArchivePlanNotFoundException();
            }

            if (plan.ExpiresAt <= clock.GetUtcNow())
            {
                if (_operationIdsByPlan.TryGetValue(planId, out var binding))
                {
                    if (!binding.IsRegistered || operations.Contains(binding.OperationId))
                    {
                        return plan;
                    }

                    _operationIdsByPlan.Remove(planId);
                    _plans.Remove(planId);
                    throw new ArchivePlanNotFoundException();
                }

                _plans.Remove(planId);
                throw new ArchivePlanExpiredException();
            }

            return plan;
        }
    }

    public string BindOperation(string planId, string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        lock (_gate)
        {
            var plan = GetRequiredPlan(planId);
            if (!plan.CanExecute)
            {
                ThrowHighestPriorityIssue(plan);
            }

            if (_operationIdsByPlan.TryGetValue(planId, out var existing))
            {
                return existing.OperationId;
            }

            _operationIdsByPlan.Add(planId, new(operationId, IsRegistered: false));
            return operationId;
        }
    }

    public void CommitBinding(string planId, string operationId)
    {
        lock (_gate)
        {
            if (!_operationIdsByPlan.TryGetValue(planId, out var existing) ||
                !existing.OperationId.Equals(operationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The archive extraction operation binding is not reserved.");
            }

            _operationIdsByPlan[planId] = existing with { IsRegistered = true };
        }
    }

    public bool ReleaseBinding(string planId, string operationId)
    {
        lock (_gate)
        {
            if (!_operationIdsByPlan.TryGetValue(planId, out var existing) ||
                !existing.OperationId.Equals(operationId, StringComparison.Ordinal))
            {
                return false;
            }

            _operationIdsByPlan.Remove(planId);
            if (_plans.TryGetValue(planId, out var plan) && plan.ExpiresAt <= clock.GetUtcNow())
            {
                _plans.Remove(planId);
            }

            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var plan in _plans.Values.Where(plan => plan.ExpiresAt <= now).ToArray())
        {
            if (_operationIdsByPlan.TryGetValue(plan.PlanId, out var binding))
            {
                if (!binding.IsRegistered || operations.Contains(binding.OperationId))
                {
                    continue;
                }

                _operationIdsByPlan.Remove(plan.PlanId);
            }

            _plans.Remove(plan.PlanId);
        }
    }

    private static void ThrowHighestPriorityIssue(ArchiveExtractionPlan plan)
    {
        var issue = plan.Violations.FirstOrDefault() ?? plan.Conflicts.FirstOrDefault();
        if (issue is null)
        {
            throw new ArchiveEntryUnsafeException();
        }

        switch (issue.Code)
        {
            case "archive_destination_conflict":
                throw new ArchiveDestinationConflictException(issue.LogicalPaths);
            case "archive_limit_exceeded":
                throw new ArchiveLimitExceededException(issue.Message);
            case "archive_entry_unsafe":
                throw new ArchiveEntryUnsafeException();
            default:
                throw new ArchivePlanIssueException(issue.Code, issue.Message);
        }
    }

    private sealed class ArchivePlanIssueException(string code, string detail)
        : ArchiveException(code, detail);

    private sealed record OperationBinding(string OperationId, bool IsRegistered);
}
