using Microsoft.Extensions.Logging;
using ReachCommander.Application.BatchRenames;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed record ExecutedRename(
    string OriginalLogicalPath,
    string FinalLogicalPath,
    string OriginalPhysicalPath,
    string FinalPhysicalPath,
    FileEntryType Type,
    EntryFingerprint PostExecutionFingerprint);

internal sealed record BatchRenameExecutionOutcome(
    BatchRenameOperationResult Result,
    IReadOnlyList<ExecutedRename> ExecutedEntries);

internal sealed class BatchRenameExecutor(
    BatchRenamePlanner planner,
    IPathSecurityService pathSecurity,
    IBatchRenameFileSystem fileSystem,
    DirectoryMutationLock directoryMutationLock,
    ILogger<BatchRenameExecutor> logger)
{
    public async ValueTask<BatchRenameExecutionOutcome> ExecuteAsync(
        Guid operationId,
        StoredBatchRenamePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await using var mutationLease = await directoryMutationLock
            .AcquireAsync(plan.SourceId, plan.DirectoryLogicalPath, cancellationToken)
            .ConfigureAwait(false);
        await planner.RevalidateAsync(plan, cancellationToken).ConfigureAwait(false);

        var ready = plan.Entries
            .Where(entry => entry.Status == BatchRenamePreviewStatus.Ready)
            .ToArray();
        var temporaryEntries = await CreateTemporaryEntriesAsync(
            operationId,
            plan,
            ready,
            cancellationToken).ConfigureAwait(false);
        LogPlannedMapping(operationId, plan, temporaryEntries);

        cancellationToken.ThrowIfCancellationRequested();
        var completedMoves = new List<CompletedMove>(ready.Length * 2);
        ExecutedRename[] executed;
        try
        {
            foreach (var entry in temporaryEntries)
            {
                fileSystem.Move(
                    entry.Planned.OldPhysicalPath,
                    entry.TemporaryPhysicalPath,
                    entry.Planned.Type);
                completedMoves.Add(new CompletedMove(
                    entry.Planned.OldPhysicalPath,
                    entry.TemporaryPhysicalPath,
                    entry.Planned.Type));
            }

            foreach (var entry in temporaryEntries)
            {
                fileSystem.Move(
                    entry.TemporaryPhysicalPath,
                    entry.Planned.NewPhysicalPath,
                    entry.Planned.Type);
                completedMoves.Add(new CompletedMove(
                    entry.TemporaryPhysicalPath,
                    entry.Planned.NewPhysicalPath,
                    entry.Planned.Type));
            }

            executed = temporaryEntries.Select(entry =>
            {
                var snapshot = fileSystem.GetEntry(
                    entry.Planned.NewLogicalPath,
                    entry.Planned.NewPhysicalPath);
                return new ExecutedRename(
                    entry.Planned.OldLogicalPath,
                    entry.Planned.NewLogicalPath,
                    entry.Planned.OldPhysicalPath,
                    entry.Planned.NewPhysicalPath,
                    entry.Planned.Type,
                    snapshot.Fingerprint);
            }).ToArray();
        }
        catch (Exception exception) when (IsExpectedMutationFailure(exception))
        {
            logger.LogWarning(
                "Batch rename {OperationId} failed for source {SourceId} directory {LogicalDirectory} after {CompletedMoveCount} moves with {ExceptionType} ({HResult}).",
                operationId,
                plan.SourceId,
                plan.DirectoryLogicalPath,
                completedMoves.Count,
                exception.GetType().Name,
                exception.HResult);
            return Compensate(operationId, plan, temporaryEntries, completedMoves);
        }
        var result = new BatchRenameOperationResult(
            operationId,
            BatchRenameOperationStatus.Completed,
            plan.Entries.Select(CompletedRow).ToArray(),
            CompensationAttempted: false,
            RecoveryRequired: false,
            UndoAvailable: true,
            UndoExpiresAt: null);
        return new BatchRenameExecutionOutcome(result, executed);
    }

    private async ValueTask<IReadOnlyList<TemporaryRename>> CreateTemporaryEntriesAsync(
        Guid operationId,
        StoredBatchRenamePlan plan,
        IReadOnlyList<PlannedRename> ready,
        CancellationToken cancellationToken)
    {
        var temporaryEntries = new List<TemporaryRename>(ready.Count);
        for (var index = 0; index < ready.Count; index++)
        {
            var temporaryName = $".reachcommander-rename-{operationId:N}-{index:D5}.tmp";
            var resolved = await pathSecurity.ResolveChildAsync(
                plan.SourceId,
                plan.DirectoryLogicalPath,
                temporaryName,
                cancellationToken).ConfigureAwait(false);
            if (fileSystem.EntryExists(resolved.PhysicalPath))
            {
                throw new RenamePlanStaleException(
                    "A reserved rename workspace entry already exists. Refresh the preview and try again.");
            }

            temporaryEntries.Add(new TemporaryRename(
                ready[index],
                resolved.LogicalPath,
                resolved.PhysicalPath));
        }

        return temporaryEntries;
    }

    private void LogPlannedMapping(
        Guid operationId,
        StoredBatchRenamePlan plan,
        IReadOnlyList<TemporaryRename> entries)
    {
        var mappings = entries.Select(entry => new
        {
            Original = entry.Planned.OldLogicalPath,
            Temporary = entry.TemporaryLogicalPath,
            Final = entry.Planned.NewLogicalPath,
        }).ToArray();
        logger.LogInformation(
            "Starting batch rename {OperationId} for source {SourceId} directory {LogicalDirectory} with logical mappings {@Mappings}.",
            operationId,
            plan.SourceId,
            plan.DirectoryLogicalPath,
            mappings);
    }

    private BatchRenameExecutionOutcome Compensate(
        Guid operationId,
        StoredBatchRenamePlan plan,
        IReadOnlyList<TemporaryRename> temporaryEntries,
        IReadOnlyList<CompletedMove> completedMoves)
    {
        var compensationAttempted = completedMoves.Count > 0;
        var compensationFailed = false;
        foreach (var move in completedMoves.Reverse())
        {
            try
            {
                fileSystem.Move(move.ToPhysicalPath, move.FromPhysicalPath, move.Type);
            }
            catch (Exception exception) when (IsExpectedMutationFailure(exception))
            {
                compensationFailed = true;
                logger.LogError(
                    "Compensation failed for batch rename {OperationId} in source {SourceId} directory {LogicalDirectory} with {ExceptionType} ({HResult}).",
                    operationId,
                    plan.SourceId,
                    plan.DirectoryLogicalPath,
                    exception.GetType().Name,
                    exception.HResult);
            }
        }

        if (!compensationFailed)
        {
            var failedRows = plan.Entries.Select(entry => entry.Status == BatchRenamePreviewStatus.Ready
                ? ResultRow(
                    entry,
                    entry.OldLogicalPath,
                    entry.OldName,
                    BatchRenameRowResult.RolledBack,
                    "The rename failed and all changes were rolled back.")
                : CompletedRow(entry)).ToArray();
            return new BatchRenameExecutionOutcome(
                new BatchRenameOperationResult(
                    operationId,
                    BatchRenameOperationStatus.Failed,
                    failedRows,
                    compensationAttempted,
                    RecoveryRequired: false,
                    UndoAvailable: false,
                    UndoExpiresAt: null),
                []);
        }

        var temporaryByOldPath = temporaryEntries.ToDictionary(
            entry => entry.Planned.OldLogicalPath,
            StringComparer.Ordinal);
        var recoveryRows = plan.Entries.Select(entry =>
        {
            if (entry.Status != BatchRenamePreviewStatus.Ready)
            {
                return CompletedRow(entry);
            }

            var temporary = temporaryByOldPath[entry.OldLogicalPath];
            var location = CurrentLocation(entry, temporary);
            return ResultRow(
                entry,
                location.LogicalPath,
                location.Name,
                BatchRenameRowResult.RecoveryRequired,
                "Automatic rollback was incomplete. Review this entry before making more changes.");
        }).ToArray();
        return new BatchRenameExecutionOutcome(
            new BatchRenameOperationResult(
                operationId,
                BatchRenameOperationStatus.RecoveryRequired,
                recoveryRows,
                compensationAttempted,
                RecoveryRequired: true,
                UndoAvailable: false,
                UndoExpiresAt: null),
            []);
    }

    private CurrentEntryLocation CurrentLocation(
        PlannedRename entry,
        TemporaryRename temporary)
    {
        if (fileSystem.EntryExists(entry.OldPhysicalPath))
        {
            return new CurrentEntryLocation(entry.OldLogicalPath, entry.OldName);
        }

        if (fileSystem.EntryExists(entry.NewPhysicalPath))
        {
            return new CurrentEntryLocation(entry.NewLogicalPath, entry.NewName);
        }

        if (fileSystem.EntryExists(temporary.TemporaryPhysicalPath))
        {
            return new CurrentEntryLocation(
                temporary.TemporaryLogicalPath,
                Path.GetFileName(temporary.TemporaryLogicalPath));
        }

        return new CurrentEntryLocation(entry.OldLogicalPath, entry.OldName);
    }

    private static BatchRenameOperationRow CompletedRow(PlannedRename entry) =>
        entry.Status == BatchRenamePreviewStatus.Ready
            ? ResultRow(
                entry,
                entry.NewLogicalPath,
                entry.NewName,
                BatchRenameRowResult.Completed,
                Message: null)
            : ResultRow(
                entry,
                entry.OldLogicalPath,
                entry.OldName,
                BatchRenameRowResult.Unchanged,
                entry.Message);

    private static BatchRenameOperationRow ResultRow(
        PlannedRename entry,
        string currentPath,
        string currentName,
        BatchRenameRowResult result,
        string? Message) => new(
            entry.OldLogicalPath,
            entry.NewLogicalPath,
            currentPath,
            entry.OldName,
            entry.NewName,
            currentName,
            entry.Type,
            result,
            Message);

    private static bool IsExpectedMutationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or
            FileAccessException or SourceNotFoundException or BatchRenameException;

    private sealed record TemporaryRename(
        PlannedRename Planned,
        string TemporaryLogicalPath,
        string TemporaryPhysicalPath);

    private sealed record CompletedMove(
        string FromPhysicalPath,
        string ToPhysicalPath,
        FileEntryType Type);

    private sealed record CurrentEntryLocation(string LogicalPath, string Name);
}
