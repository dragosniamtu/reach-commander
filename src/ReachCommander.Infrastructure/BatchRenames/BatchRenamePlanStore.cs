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

internal sealed record StoredBatchRenameOperation(
    Guid OperationId,
    Guid PlanId,
    DateTimeOffset CompletedAt,
    DateTimeOffset UndoExpiresAt,
    string SourceId,
    string DirectoryLogicalPath,
    string DirectoryPhysicalPath,
    IReadOnlyList<ExecutedRename> Entries,
    BatchRenameOperationResult ExecuteResult,
    BatchRenameOperationResult? UndoResult);

internal sealed class BatchRenamePlanStore(TimeProvider clock)
{
    private const int MaximumPlans = 256;
    private const int MaximumOperations = 128;
    private readonly ConcurrentDictionary<Guid, StoredBatchRenamePlan> _plans = new();
    private readonly ConcurrentDictionary<Guid, StoredBatchRenameOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, Guid> _operationIdsByPlan = new();
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

    public bool TryGetOperationForPlan(
        Guid planId,
        out StoredBatchRenameOperation? operation)
    {
        lock (_gate)
        {
            if (!_operationIdsByPlan.TryGetValue(planId, out var operationId) ||
                !_operations.TryGetValue(operationId, out operation))
            {
                operation = null;
                return false;
            }

            if (operation.UndoExpiresAt <= clock.GetUtcNow())
            {
                RemoveOperation(operation);
                operation = null;
                return false;
            }

            return true;
        }
    }

    public StoredBatchRenameOperation GetRequiredOperation(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation))
            {
                throw new RenamePlanNotFoundException("The rename operation was not found.");
            }

            if (operation.UndoExpiresAt <= clock.GetUtcNow())
            {
                RemoveOperation(operation);
                throw new RenamePlanExpiredException("The rename operation has expired.");
            }

            return operation;
        }
    }

    public StoredBatchRenameOperation SaveOperation(StoredBatchRenameOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            RemoveExpiredOperations(clock.GetUtcNow());
            if (_operationIdsByPlan.TryGetValue(operation.PlanId, out var existingId) &&
                _operations.TryGetValue(existingId, out var existing))
            {
                return existing;
            }

            _operations[operation.OperationId] = operation;
            _operationIdsByPlan[operation.PlanId] = operation.OperationId;
            while (_operations.Count > MaximumOperations)
            {
                var oldest = _operations.Values.MinBy(candidate => candidate.CompletedAt);
                if (oldest is null)
                {
                    break;
                }

                RemoveOperation(oldest);
            }

            return operation;
        }
    }

    public BatchRenameOperationResult SaveUndoResult(
        Guid operationId,
        BatchRenameOperationResult undoResult)
    {
        ArgumentNullException.ThrowIfNull(undoResult);
        lock (_gate)
        {
            var operation = GetRequiredOperation(operationId);
            if (operation.UndoResult is not null)
            {
                return operation.UndoResult;
            }

            _operations[operationId] = operation with { UndoResult = undoResult };
            return undoResult;
        }
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

    private void RemoveExpiredOperations(DateTimeOffset now)
    {
        foreach (var operation in _operations.Values)
        {
            if (operation.UndoExpiresAt <= now)
            {
                RemoveOperation(operation);
            }
        }
    }

    private void RemoveOperation(StoredBatchRenameOperation operation)
    {
        _operations.TryRemove(operation.OperationId, out _);
        _operationIdsByPlan.TryRemove(
            new KeyValuePair<Guid, Guid>(operation.PlanId, operation.OperationId));
    }
}
