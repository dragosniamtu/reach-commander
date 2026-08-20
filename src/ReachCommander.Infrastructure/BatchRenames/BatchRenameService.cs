using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed class BatchRenameService(
    BatchRenamePlanner planner,
    BatchRenamePlanStore planStore,
    BatchRenameExecutor executor,
    BatchRenameRequestLock requestLock,
    TimeProvider clock) : IBatchRenameService
{
    private static readonly TimeSpan OperationLifetime = TimeSpan.FromMinutes(30);

    public ValueTask<BatchRenamePreview> PreviewAsync(
        BatchRenamePreviewCommand command,
        CancellationToken cancellationToken) =>
        planner.PreviewAsync(command, cancellationToken);

    public async ValueTask<BatchRenameOperationResult> ExecuteAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        if (planStore.TryGetOperationForPlan(planId, out var existing))
        {
            return existing!.ExecuteResult;
        }

        await using var requestLease = await requestLock
            .AcquirePlanAsync(planId, cancellationToken)
            .ConfigureAwait(false);
        if (planStore.TryGetOperationForPlan(planId, out existing))
        {
            return existing!.ExecuteResult;
        }

        var plan = planStore.GetRequiredPlan(planId);
        if (!plan.Preview.CanExecute)
        {
            throw new InvalidRenameRuleException(
                "The rename preview contains unchanged, invalid, or conflicting entries.");
        }

        var operationId = Guid.NewGuid();
        var outcome = await executor
            .ExecuteAsync(operationId, plan, cancellationToken)
            .ConfigureAwait(false);
        var completedAt = clock.GetUtcNow();
        var retentionExpiresAt = completedAt.Add(OperationLifetime);
        var undoAvailable = outcome.Result.Status == BatchRenameOperationStatus.Completed &&
            outcome.ExecutedEntries.Count > 0;
        var executeResult = outcome.Result with
        {
            UndoAvailable = undoAvailable,
            UndoExpiresAt = undoAvailable ? retentionExpiresAt : null,
        };
        var stored = planStore.SaveOperation(new StoredBatchRenameOperation(
            operationId,
            planId,
            completedAt,
            retentionExpiresAt,
            plan.SourceId,
            plan.DirectoryLogicalPath,
            plan.DirectoryPhysicalPath,
            outcome.ExecutedEntries,
            executeResult,
            UndoResult: null));
        return stored.ExecuteResult;
    }

    public async ValueTask<BatchRenameOperationResult> UndoAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = planStore.GetRequiredOperation(operationId);
        if (operation.UndoResult is not null)
        {
            return operation.UndoResult;
        }

        await using var requestLease = await requestLock
            .AcquireUndoAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        operation = planStore.GetRequiredOperation(operationId);
        if (operation.UndoResult is not null)
        {
            return operation.UndoResult;
        }

        if (operation.ExecuteResult.Status != BatchRenameOperationStatus.Completed ||
            operation.Entries.Count == 0)
        {
            throw new InvalidRenameRuleException("Only a completed rename operation can be undone.");
        }

        var reversePlan = CreateReversePlan(operation);
        var outcome = await executor
            .ExecuteAsync(operationId, reversePlan, cancellationToken)
            .ConfigureAwait(false);
        var undoResult = outcome.Result with
        {
            Status = outcome.Result.Status == BatchRenameOperationStatus.Completed
                ? BatchRenameOperationStatus.Undone
                : outcome.Result.Status,
            UndoAvailable = false,
            UndoExpiresAt = null,
        };
        return planStore.SaveUndoResult(operationId, undoResult);
    }

    private StoredBatchRenamePlan CreateReversePlan(StoredBatchRenameOperation operation)
    {
        var planId = Guid.NewGuid();
        var createdAt = clock.GetUtcNow();
        var entries = operation.Entries.Select(entry => new PlannedRename(
            entry.FinalLogicalPath,
            entry.OriginalLogicalPath,
            entry.FinalPhysicalPath,
            entry.OriginalPhysicalPath,
            FileName(entry.FinalLogicalPath),
            FileName(entry.OriginalLogicalPath),
            entry.Type,
            entry.PostExecutionFingerprint,
            BatchRenamePreviewStatus.Ready,
            Message: null)).ToArray();
        var rows = entries.Select(entry => new BatchRenamePreviewRow(
            entry.OldLogicalPath,
            entry.OldName,
            Extension(entry.OldName, entry.Type),
            entry.NewName,
            entry.Type,
            entry.PreviewFingerprint.Length,
            entry.PreviewFingerprint.ModifiedAt,
            BatchRenamePreviewStatus.Ready,
            Message: null)).ToArray();
        var preview = new BatchRenamePreview(
            planId,
            operation.UndoExpiresAt,
            rows,
            CanExecute: entries.Length > 0,
            ChangedCount: entries.Length,
            UnchangedCount: 0,
            InvalidCount: 0);
        return new StoredBatchRenamePlan(
            planId,
            createdAt,
            operation.UndoExpiresAt,
            operation.SourceId,
            operation.DirectoryLogicalPath,
            operation.DirectoryPhysicalPath,
            entries,
            preview);
    }

    private static string FileName(string logicalPath) =>
        logicalPath[(logicalPath.LastIndexOf('/') + 1)..];

    private static string? Extension(string name, ReachCommander.Domain.Files.FileEntryType type)
    {
        if (type != ReachCommander.Domain.Files.FileEntryType.File)
        {
            return null;
        }

        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) || extension.Length == name.Length
            ? null
            : extension[1..];
    }
}
