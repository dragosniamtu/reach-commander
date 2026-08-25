using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.FileOperations;
using ReachCommander.Infrastructure.FileOperations.Execution;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.Trash;

internal sealed class TrashOperationExecutor(
    IPathSecurityService pathSecurity,
    IFileOperationInspector inspector,
    IFileOperationFileSystem fileSystem,
    DirectoryMutationLock mutationLock,
    TrashManifestStore manifestStore,
    FileOperationRepository repository,
    TimeProvider clock) : ITrashOperationExecutor
{
    public async Task<FileOperationStatus> ExecuteAsync(
        PersistedFileOperationDocument claimed,
        CancellationToken cancellationToken)
    {
        if (claimed.Plan.Kind is not (
            FileOperationKind.Trash or
            FileOperationKind.Restore or
            FileOperationKind.PermanentDelete or
            FileOperationKind.EmptyTrash))
        {
            throw new InvalidOperationException("The Trash executor received an unsupported operation kind.");
        }

        try
        {
            if (claimed.Plan.Kind is FileOperationKind.PermanentDelete or FileOperationKind.EmptyTrash &&
                !claimed.Approval.PermanentDeleteConfirmed)
            {
                throw new PermanentDeleteConfirmationRequiredException();
            }

            await using var lease = await mutationLock.AcquireManyAsync(
                claimed.Plan.LockTargets,
                cancellationToken);
            await ThrowIfCancellationRequestedAsync(claimed.OperationId, cancellationToken);
            var running = await repository.UpdateAsync(
                claimed.OperationId,
                document => document with
                {
                    Status = document.Status with { Phase = FileOperationPhase.Running },
                },
                cancellationToken);
            return running.Plan.Kind switch
            {
                FileOperationKind.Trash => await ExecuteTrashAsync(running, cancellationToken),
                FileOperationKind.Restore => await ExecuteRestoreAsync(running, cancellationToken),
                FileOperationKind.PermanentDelete when running.Plan.TrashIds.Count == 0 =>
                    await ExecuteDirectPermanentDeleteAsync(running, cancellationToken),
                FileOperationKind.PermanentDelete or FileOperationKind.EmptyTrash =>
                    await ExecuteTrashRecordDeletionAsync(running, cancellationToken),
                _ => throw new InvalidOperationException(),
            };
        }
        catch (FileOperationCancelledException)
        {
            return await MarkCancelledAsync(claimed.OperationId);
        }
        catch (FileOperationException exception)
        {
            return await MarkFailedAsync(claimed.OperationId, exception.PublicDetail);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return await MarkFailedAsync(
                claimed.OperationId,
                "The Trash operation could not be completed.");
        }
    }

    private async Task<FileOperationStatus> ExecuteTrashAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var plan = operation.Plan;
        if (plan.SourceId is null || plan.Entries.Count != plan.TrashIds.Count)
        {
            throw new TrashManifestInvalidException();
        }

        var outcomes = new List<FileOperationItemOutcome>();
        long completedBytes = 0;
        for (var index = 0; index < plan.Entries.Count; index++)
        {
            await ThrowIfCancellationRequestedAsync(operation.OperationId, cancellationToken);
            var entry = plan.Entries[index];
            try
            {
                var current = await inspector.GetRequiredAsync(
                    plan.SourceId,
                    entry.SourceLogicalPath,
                    cancellationToken);
                if (current.Fingerprint != entry.Fingerprint || current.IsSymbolicLink)
                {
                    throw new OperationPlanStaleException();
                }

                var source = await pathSecurity.ResolveAsync(
                    plan.SourceId,
                    entry.SourceLogicalPath,
                    cancellationToken);
                var trashId = plan.TrashIds[index];
                var paths = await manifestStore.GetOrCreatePathsAsync(
                    plan.SourceId,
                    trashId,
                    cancellationToken);
                if (Directory.Exists(paths.StagingContainerPhysicalPath) ||
                    Directory.Exists(paths.ItemContainerPhysicalPath) ||
                    File.Exists(paths.StagingContainerPhysicalPath) ||
                    File.Exists(paths.ItemContainerPhysicalPath))
                {
                    throw new TrashManifestInvalidException();
                }

                Directory.CreateDirectory(paths.StagingContainerPhysicalPath);
                var movedToStaging = false;
                var movedToItems = false;
                try
                {
                    if (fileSystem.TryMove(
                            source.PhysicalPath,
                            paths.StagingItemPhysicalPath) != MoveAttempt.Moved)
                    {
                        throw new IOException("Source-local Trash crossed filesystems.");
                    }

                    movedToStaging = true;
                    Directory.Move(
                        paths.StagingContainerPhysicalPath,
                        paths.ItemContainerPhysicalPath);
                    movedToItems = true;
                    var manifest = new TrashManifest(
                        TrashManifest.CurrentSchemaVersion,
                        trashId,
                        plan.SourceId,
                        entry.SourceLogicalPath,
                        Name(entry.SourceLogicalPath),
                        entry.Fingerprint.Type,
                        entry.Fingerprint.Length,
                        clock.GetUtcNow(),
                        $"items/{trashId:N}/item",
                        entry.Fingerprint);
                    await manifestStore.WriteManifestAsync(manifest, cancellationToken);
                }
                catch
                {
                    var storedPath = movedToItems
                        ? paths.ItemPhysicalPath
                        : paths.StagingItemPhysicalPath;
                    if ((movedToStaging || movedToItems) &&
                        fileSystem.Exists(storedPath) &&
                        !fileSystem.Exists(source.PhysicalPath))
                    {
                        _ = fileSystem.TryMove(storedPath, source.PhysicalPath);
                    }

                    DeleteEmpty(paths.StagingContainerPhysicalPath);
                    DeleteEmpty(paths.ItemContainerPhysicalPath);
                    throw;
                }

                completedBytes = checked(completedBytes + (entry.Fingerprint.Length ?? 0));
                outcomes.Add(Outcome(
                    plan.SourceId,
                    entry.SourceLogicalPath,
                    null,
                    null,
                    FileOperationItemResult.Completed,
                    null,
                    null));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(Outcome(
                    plan.SourceId,
                    entry.SourceLogicalPath,
                    null,
                    null,
                    FileOperationItemResult.Failed,
                    "file_operation_failed",
                    "The item could not be moved to Trash."));
            }

            await SaveProgressAsync(
                operation.OperationId,
                index + 1,
                plan.Entries.Count,
                completedBytes,
                plan.TotalBytes,
                outcomes,
                cancellationToken);
        }

        return await CompleteAsync(operation.OperationId, outcomes, plan.TotalBytes);
    }

    private async Task<FileOperationStatus> ExecuteRestoreAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var plan = operation.Plan;
        if (plan.Entries.Count != plan.TrashIds.Count)
        {
            throw new TrashManifestInvalidException();
        }

        var resolutions = operation.Approval.Resolutions.ToDictionary(
            resolution => resolution.ConflictId,
            resolution => resolution.Decision);
        ValidateResolutionSet(plan, resolutions);
        var outcomes = new List<FileOperationItemOutcome>();
        long completedBytes = 0;
        for (var index = 0; index < plan.TrashIds.Count; index++)
        {
            await ThrowIfCancellationRequestedAsync(operation.OperationId, cancellationToken);
            var entry = plan.Entries[index];
            var record = await manifestStore.GetRequiredAsync(plan.TrashIds[index], cancellationToken);
            if (record.Manifest.Fingerprint != entry.Fingerprint ||
                !record.Manifest.OriginalLogicalPath.Equals(
                    entry.DestinationLogicalPath,
                    StringComparison.Ordinal))
            {
                throw new TrashManifestInvalidException();
            }

            var currentDestination = await inspector.TryGetAsync(
                record.Manifest.SourceId,
                entry.DestinationLogicalPath,
                cancellationToken);
            if (currentDestination?.Fingerprint != entry.DestinationFingerprint)
            {
                throw new TrashRestoreConflictException();
            }

            var decision = entry.ConflictId is null
                ? (FileOperationConflictDecision?)null
                : resolutions[entry.ConflictId.Value];
            if (decision == FileOperationConflictDecision.Skip)
            {
                outcomes.Add(Outcome(
                    record.Manifest.SourceId,
                    record.Manifest.OriginalLogicalPath,
                    record.Manifest.SourceId,
                    entry.DestinationLogicalPath,
                    FileOperationItemResult.Skipped,
                    null,
                    null));
                await SaveProgressAsync(
                    operation.OperationId,
                    index + 1,
                    plan.Entries.Count,
                    completedBytes,
                    plan.TotalBytes,
                    outcomes,
                    cancellationToken);
                continue;
            }

            var destinationLogicalPath = entry.DestinationLogicalPath;
            await EnsureParentsAsync(
                record.Manifest.SourceId,
                Parent(destinationLogicalPath),
                cancellationToken);
            if (decision == FileOperationConflictDecision.CreateUniqueName)
            {
                destinationLogicalPath = await FindUniqueAsync(
                    record.Manifest.SourceId,
                    destinationLogicalPath,
                    cancellationToken);
            }

            var destination = await ResolveLogicalCandidateAsync(
                record.Manifest.SourceId,
                destinationLogicalPath,
                cancellationToken);
            Quarantine? quarantine = null;
            try
            {
                if (fileSystem.Exists(destination.PhysicalPath))
                {
                    if (decision != FileOperationConflictDecision.Overwrite)
                    {
                        throw new TrashRestoreConflictException();
                    }

                    quarantine = await QuarantineAsync(
                        operation.OperationId,
                        record.Manifest.SourceId,
                        destinationLogicalPath,
                        destination.PhysicalPath,
                        cancellationToken);
                }

                if (fileSystem.TryMove(
                        record.Paths.ItemPhysicalPath,
                        destination.PhysicalPath) != MoveAttempt.Moved)
                {
                    throw new IOException("Source-local Trash restore crossed filesystems.");
                }

                if (quarantine is not null)
                {
                    SafeDeleteTree(quarantine.PhysicalPath);
                    await RemoveJournalAsync(
                        operation.OperationId,
                        quarantine.OwnedName,
                        cancellationToken);
                }

                manifestStore.RemoveMetadata(record);
                completedBytes = checked(completedBytes + (record.Manifest.Size ?? 0));
                outcomes.Add(Outcome(
                    record.Manifest.SourceId,
                    record.Manifest.OriginalLogicalPath,
                    record.Manifest.SourceId,
                    destinationLogicalPath,
                    FileOperationItemResult.Completed,
                    null,
                    null));
            }
            catch
            {
                if (quarantine is not null &&
                    !fileSystem.Exists(destination.PhysicalPath) &&
                    fileSystem.Exists(quarantine.PhysicalPath))
                {
                    _ = fileSystem.TryMove(quarantine.PhysicalPath, destination.PhysicalPath);
                    await RemoveJournalAsync(
                        operation.OperationId,
                        quarantine.OwnedName,
                        CancellationToken.None);
                }

                throw;
            }

            await SaveProgressAsync(
                operation.OperationId,
                index + 1,
                plan.Entries.Count,
                completedBytes,
                plan.TotalBytes,
                outcomes,
                cancellationToken);
        }

        return await CompleteAsync(operation.OperationId, outcomes, plan.TotalBytes);
    }

    private async Task<FileOperationStatus> ExecuteDirectPermanentDeleteAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var plan = operation.Plan;
        foreach (var entry in plan.Entries)
        {
            var current = await inspector.GetRequiredAsync(
                plan.SourceId!,
                entry.SourceLogicalPath,
                cancellationToken);
            if (current.Fingerprint != entry.Fingerprint || current.IsSymbolicLink)
            {
                throw new OperationPlanStaleException();
            }
        }

        var outcomes = new List<FileOperationItemOutcome>();
        long completedBytes = 0;
        var completedItems = 0;
        foreach (var entry in plan.Entries.Reverse())
        {
            await ThrowIfCancellationRequestedAsync(operation.OperationId, cancellationToken);
            try
            {
                var resolved = await pathSecurity.ResolveAsync(
                    plan.SourceId!,
                    entry.SourceLogicalPath,
                    cancellationToken);
                if (entry.Fingerprint.Type == FileEntryType.Directory)
                {
                    fileSystem.DeleteDirectory(resolved.PhysicalPath, recursive: false);
                }
                else
                {
                    fileSystem.DeleteFile(resolved.PhysicalPath);
                }

                completedBytes = checked(completedBytes + (entry.Fingerprint.Length ?? 0));
                outcomes.Add(Outcome(
                    plan.SourceId!,
                    entry.SourceLogicalPath,
                    null,
                    null,
                    FileOperationItemResult.Completed,
                    null,
                    null));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(Outcome(
                    plan.SourceId!,
                    entry.SourceLogicalPath,
                    null,
                    null,
                    FileOperationItemResult.Failed,
                    "file_operation_failed",
                    "The item could not be permanently deleted."));
            }

            completedItems++;
            await SaveProgressAsync(
                operation.OperationId,
                completedItems,
                plan.Entries.Count,
                completedBytes,
                plan.TotalBytes,
                outcomes,
                cancellationToken);
        }

        return await CompleteAsync(operation.OperationId, outcomes, plan.TotalBytes);
    }

    private async Task<FileOperationStatus> ExecuteTrashRecordDeletionAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var plan = operation.Plan;
        var outcomes = new List<FileOperationItemOutcome>();
        long completedBytes = 0;
        for (var index = 0; index < plan.TrashIds.Count; index++)
        {
            await ThrowIfCancellationRequestedAsync(operation.OperationId, cancellationToken);
            var record = await manifestStore.GetRequiredAsync(plan.TrashIds[index], cancellationToken);
            if (plan.TrashSourceScope is not null &&
                !record.Manifest.SourceId.Equals(plan.TrashSourceScope, StringComparison.Ordinal))
            {
                throw new TrashManifestInvalidException();
            }

            try
            {
                SafeDeleteTree(record.Paths.ItemPhysicalPath);
                manifestStore.RemoveMetadata(record);
                completedBytes = checked(completedBytes + (record.Manifest.Size ?? 0));
                outcomes.Add(Outcome(
                    record.Manifest.SourceId,
                    record.Manifest.OriginalLogicalPath,
                    null,
                    null,
                    FileOperationItemResult.Completed,
                    null,
                    null));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(Outcome(
                    record.Manifest.SourceId,
                    record.Manifest.OriginalLogicalPath,
                    null,
                    null,
                    FileOperationItemResult.Failed,
                    "file_operation_failed",
                    "The Trash item could not be permanently deleted."));
            }

            await SaveProgressAsync(
                operation.OperationId,
                index + 1,
                plan.TrashIds.Count,
                completedBytes,
                plan.TotalBytes,
                outcomes,
                cancellationToken);
        }

        return await CompleteAsync(operation.OperationId, outcomes, plan.TotalBytes);
    }

    private async Task EnsureParentsAsync(
        string sourceId,
        string logicalParent,
        CancellationToken cancellationToken)
    {
        var root = await pathSecurity.ResolveAsync(sourceId, "/", cancellationToken);
        var currentPhysical = root.PhysicalPath;
        var currentLogical = "/";
        foreach (var segment in logicalParent.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            currentPhysical = Path.Combine(currentPhysical, segment);
            currentLogical = currentLogical == "/" ? $"/{segment}" : $"{currentLogical}/{segment}";
            if (Directory.Exists(currentPhysical))
            {
                var attributes = fileSystem.GetAttributes(currentPhysical);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new UnsafeSymbolicLinkException();
                }

                continue;
            }

            if (File.Exists(currentPhysical))
            {
                throw new TrashRestoreConflictException();
            }

            Directory.CreateDirectory(currentPhysical);
        }
    }

    private async Task<ResolvedSourcePath> ResolveLogicalCandidateAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken) =>
        await pathSecurity.ResolveChildAsync(
            sourceId,
            Parent(logicalPath),
            Name(logicalPath),
            cancellationToken);

    private async Task<string> FindUniqueAsync(
        string sourceId,
        string requestedLogicalPath,
        CancellationToken cancellationToken)
    {
        var parent = await pathSecurity.ResolveAsync(
            sourceId,
            Parent(requestedLogicalPath),
            cancellationToken);
        return UniqueNamePolicy.Find(
            requestedLogicalPath,
            candidate => fileSystem.Exists(Path.Combine(parent.PhysicalPath, Name(candidate))));
    }

    private async Task<Quarantine> QuarantineAsync(
        Guid operationId,
        string sourceId,
        string destinationLogicalPath,
        string destinationPhysicalPath,
        CancellationToken cancellationToken)
    {
        var parentLogical = Parent(destinationLogicalPath);
        var parent = await pathSecurity.ResolveAsync(sourceId, parentLogical, cancellationToken);
        var ownedName =
            $"{ReservedFileOperationPathPolicy.OperationPrefix}{operationId:N}-quarantine-{Guid.NewGuid():N}";
        var ownedPath = Path.Combine(parent.PhysicalPath, ownedName);
        await AddJournalAsync(
            operationId,
            new(sourceId, parentLogical, ownedName, destinationLogicalPath, true),
            cancellationToken);
        if (fileSystem.TryMove(destinationPhysicalPath, ownedPath) != MoveAttempt.Moved)
        {
            throw new IOException("A restore quarantine crossed filesystems.");
        }

        return new(ownedName, ownedPath);
    }

    private async Task AddJournalAsync(
        Guid operationId,
        FileOperationJournalEntry entry,
        CancellationToken cancellationToken) =>
        await repository.UpdateAsync(
            operationId,
            document => document with
            {
                Journal = new(
                    operationId,
                    (document.Journal?.Entries ?? []).Append(entry).ToArray()),
            },
            cancellationToken);

    private async Task RemoveJournalAsync(
        Guid operationId,
        string ownedName,
        CancellationToken cancellationToken) =>
        await repository.UpdateAsync(
            operationId,
            document => document with
            {
                Journal = RemainingJournal(document, ownedName),
            },
            cancellationToken);

    private static FileOperationExecutionJournal? RemainingJournal(
        PersistedFileOperationDocument document,
        string ownedName)
    {
        var entries = document.Journal?.Entries
            .Where(entry => !entry.OwnedName.Equals(ownedName, StringComparison.Ordinal))
            .ToArray() ?? [];
        return entries.Length == 0 ? null : new(document.OperationId, entries);
    }

    private void SafeDeleteTree(string path)
    {
        var attributes = fileSystem.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new UnsafeSymbolicLinkException();
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            fileSystem.DeleteFile(path);
            return;
        }

        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            entry.Refresh();
            if (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new UnsafeSymbolicLinkException();
            }

            SafeDeleteTree(entry.FullName);
        }

        fileSystem.DeleteDirectory(path, recursive: false);
    }

    private async Task SaveProgressAsync(
        Guid operationId,
        int completedItems,
        int totalItems,
        long completedBytes,
        long? totalBytes,
        IReadOnlyList<FileOperationItemOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var document = await repository.GetDocumentAsync(operationId, cancellationToken);
        var elapsed = clock.GetUtcNow() - document.Status.CreatedAt;
        var progress = new FileOperationProgress(
            null,
            completedItems,
            totalItems,
            completedBytes,
            totalBytes,
            totalBytes is null
                ? null
                : totalBytes == 0
                    ? 100
                    : Math.Clamp(completedBytes * 100d / totalBytes.Value, 0, 100),
            null,
            elapsed,
            null);
        await repository.UpdateAsync(
            operationId,
            current => current with
            {
                Status = current.Status with
                {
                    Progress = progress,
                    Outcomes = outcomes.ToArray(),
                },
            },
            cancellationToken);
    }

    private async Task<FileOperationStatus> CompleteAsync(
        Guid operationId,
        IReadOnlyList<FileOperationItemOutcome> outcomes,
        long? totalBytes)
    {
        var completed = await repository.UpdateAsync(
            operationId,
            document => document with
            {
                Status = document.Status with
                {
                    Phase = outcomes.Any(outcome => outcome.Result == FileOperationItemResult.Failed)
                        ? FileOperationPhase.CompletedWithErrors
                        : FileOperationPhase.Completed,
                    Progress = document.Status.Progress with
                    {
                        CompletedBytes = totalBytes ?? document.Status.Progress.CompletedBytes,
                        Percentage = totalBytes is null ? null : 100,
                    },
                    Outcomes = outcomes.ToArray(),
                },
                Journal = null,
            },
            CancellationToken.None);
        return completed.Status;
    }

    private async Task ThrowIfCancellationRequestedAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((await repository.GetDocumentAsync(operationId, cancellationToken)).CancellationRequested)
        {
            throw new FileOperationCancelledException();
        }
    }

    private async Task<FileOperationStatus> MarkCancelledAsync(Guid operationId)
    {
        var cancelled = await repository.UpdateAsync(
            operationId,
            document => document with
            {
                Status = document.Status with { Phase = FileOperationPhase.Cancelled },
            },
            CancellationToken.None);
        return cancelled.Status;
    }

    private async Task<FileOperationStatus> MarkFailedAsync(Guid operationId, string warning)
    {
        var failed = await repository.UpdateAsync(
            operationId,
            document => document with
            {
                Status = document.Status with
                {
                    Phase = FileOperationPhase.Failed,
                    Warnings = document.Status.Warnings.Append(warning).ToArray(),
                },
            },
            CancellationToken.None);
        return failed.Status;
    }

    private static void ValidateResolutionSet(
        FileOperationPlan plan,
        IReadOnlyDictionary<Guid, FileOperationConflictDecision> resolutions)
    {
        if (resolutions.Count != plan.Conflicts.Count ||
            plan.Conflicts.Any(conflict =>
                !resolutions.TryGetValue(conflict.ConflictId, out var decision) ||
                !conflict.AllowedDecisions.Contains(decision)))
        {
            throw new DestinationConflictException();
        }
    }

    private static FileOperationItemOutcome Outcome(
        string sourceId,
        string sourceLogicalPath,
        string? destinationSourceId,
        string? destinationLogicalPath,
        FileOperationItemResult result,
        string? errorCode,
        string? detail) => new(
            sourceId,
            sourceLogicalPath,
            destinationSourceId,
            destinationLogicalPath,
            result,
            errorCode,
            detail);

    private static void DeleteEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path, recursive: false);
        }
    }

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private sealed record Quarantine(string OwnedName, string PhysicalPath);
}
