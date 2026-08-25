using ReachCommander.Application.FileOperations;
using ReachCommander.Application.Files;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.FileOperations.Planning;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.FileOperations.Execution;

internal sealed class FileOperationExecutor(
    IPathSecurityService pathSecurity,
    IFileOperationInspector inspector,
    IFileOperationFileSystem fileSystem,
    DirectoryMutationLock mutationLock,
    FileOperationRepository repository,
    TimeProvider clock)
{
    internal async Task<FileOperationStatus> ExecuteAsync(
        PersistedFileOperationDocument claimed,
        CancellationToken cancellationToken)
    {
        if (claimed.Plan.Kind != FileOperationKind.Copy)
        {
            throw new InvalidOperationException("The Copy executor received an unsupported operation kind.");
        }

        try
        {
            await using var lease = await mutationLock.AcquireManyAsync(
                claimed.Plan.LockTargets,
                cancellationToken);
            await RevalidateAsync(claimed, cancellationToken);
            var running = await repository.UpdateAsync(
                claimed.OperationId,
                document => document with
                {
                    Status = document.Status with { Phase = FileOperationPhase.Running },
                },
                cancellationToken);
            return await ExecuteCopyAsync(running, cancellationToken);
        }
        catch (FileOperationException exception)
        {
            return await MarkFailedAsync(claimed.OperationId, exception.PublicDetail, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return await MarkFailedAsync(
                claimed.OperationId,
                "The file operation could not be completed.",
                cancellationToken);
        }
    }

    private async Task<FileOperationStatus> ExecuteCopyAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var plan = operation.Plan;
        var tracker = new FileOperationProgressTracker(
            clock,
            plan.Entries.Count,
            plan.TotalBytes);
        var resolutions = operation.Approval.Resolutions.ToDictionary(
            resolution => resolution.ConflictId,
            resolution => resolution.Decision);
        var rules = new List<DestinationRule>();
        var outcomes = new List<FileOperationItemOutcome>();
        var completedItems = 0;
        long completedBytes = 0;

        foreach (var entry in plan.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rule = FindRule(rules, entry.SourceLogicalPath);
            if (rule?.Skip == true)
            {
                outcomes.Add(Outcome(
                    plan,
                    entry,
                    entry.DestinationLogicalPath,
                    FileOperationItemResult.Skipped,
                    null,
                    null));
                completedItems++;
                await SaveProgressAsync(
                    operation.OperationId,
                    tracker.Report(null, completedItems, completedBytes),
                    outcomes,
                    cancellationToken);
                continue;
            }

            var destinationLogicalPath = rule is null
                ? entry.DestinationLogicalPath
                : Remap(entry.SourceLogicalPath, rule.SourcePrefix, rule.DestinationPrefix!);
            var remappedByAncestor = rule is not null;
            var decision = entry.ConflictId is not null && !remappedByAncestor
                ? resolutions[entry.ConflictId.Value]
                : (FileOperationConflictDecision?)null;
            if (decision == FileOperationConflictDecision.Skip)
            {
                if (entry.Fingerprint.Type == FileEntryType.Directory)
                {
                    rules.Add(new(entry.SourceLogicalPath, null, Skip: true));
                }

                outcomes.Add(Outcome(
                    plan,
                    entry,
                    destinationLogicalPath,
                    FileOperationItemResult.Skipped,
                    null,
                    null));
                completedItems++;
                await SaveProgressAsync(
                    operation.OperationId,
                    tracker.Report(null, completedItems, completedBytes),
                    outcomes,
                    cancellationToken);
                continue;
            }

            if (decision == FileOperationConflictDecision.CreateUniqueName)
            {
                destinationLogicalPath = await FindUniqueDestinationAsync(
                    plan.DestinationSourceId!,
                    destinationLogicalPath,
                    cancellationToken);
                if (entry.Fingerprint.Type == FileEntryType.Directory)
                {
                    rules.Add(new(entry.SourceLogicalPath, destinationLogicalPath, Skip: false));
                }
            }

            try
            {
                if (entry.Fingerprint.Type == FileEntryType.Directory)
                {
                    await CopyDirectoryAsync(
                        operation.OperationId,
                        plan.DestinationSourceId!,
                        entry,
                        destinationLogicalPath,
                        decision,
                        cancellationToken);
                }
                else
                {
                    var copiedBytes = await CopyFileAsync(
                        operation.OperationId,
                        plan.SourceId!,
                        plan.DestinationSourceId!,
                        entry,
                        destinationLogicalPath,
                        decision,
                        completedItems,
                        completedBytes,
                        tracker,
                        outcomes,
                        cancellationToken);
                    completedBytes = checked(completedBytes + copiedBytes);
                }

                outcomes.Add(Outcome(
                    plan,
                    entry,
                    destinationLogicalPath,
                    FileOperationItemResult.Completed,
                    null,
                    null));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                completedBytes = Math.Max(completedBytes, tracker.CompletedBytes);
                if (entry.Fingerprint.Type == FileEntryType.Directory)
                {
                    rules.Add(new(entry.SourceLogicalPath, null, Skip: true));
                }

                outcomes.Add(Outcome(
                    plan,
                    entry,
                    destinationLogicalPath,
                    FileOperationItemResult.Failed,
                    "file_operation_failed",
                    "The item could not be copied."));
            }

            completedItems++;
            await SaveProgressAsync(
                operation.OperationId,
                tracker.Report(null, completedItems, completedBytes),
                outcomes,
                cancellationToken);
        }

        var phase = outcomes.Any(outcome => outcome.Result == FileOperationItemResult.Failed)
            ? FileOperationPhase.CompletedWithErrors
            : FileOperationPhase.Completed;
        var completed = await repository.UpdateAsync(
            operation.OperationId,
            document => document with
            {
                Status = document.Status with
                {
                    Phase = phase,
                    Progress = tracker.Report(null, completedItems, completedBytes),
                    Outcomes = outcomes.ToArray(),
                },
                Journal = null,
            },
            cancellationToken);
        return completed.Status;
    }

    private async Task CopyDirectoryAsync(
        Guid operationId,
        string destinationSourceId,
        PlannedFileOperationEntry entry,
        string destinationLogicalPath,
        FileOperationConflictDecision? decision,
        CancellationToken cancellationToken)
    {
        var destination = await ResolveDestinationAsync(
            destinationSourceId,
            destinationLogicalPath,
            cancellationToken);
        var existing = await inspector.TryGetAsync(
            destinationSourceId,
            destinationLogicalPath,
            cancellationToken);
        if (existing is null)
        {
            fileSystem.CreateDirectory(destination.PhysicalPath);
            return;
        }

        if (existing.Type == FileEntryType.Directory)
        {
            return;
        }

        if (decision != FileOperationConflictDecision.Overwrite)
        {
            throw new IOException("A directory destination conflict was not approved.");
        }

        var quarantine = await QuarantineAsync(
            operationId,
            destinationSourceId,
            destinationLogicalPath,
            existing.Type,
            cancellationToken);
        try
        {
            fileSystem.CreateDirectory(destination.PhysicalPath);
            DeleteOwned(quarantine.PhysicalPath, existing.Type);
            await RemoveJournalAsync(operationId, quarantine.OwnedName, cancellationToken);
        }
        catch
        {
            if (!fileSystem.Exists(destination.PhysicalPath))
            {
                _ = fileSystem.TryMove(quarantine.PhysicalPath, destination.PhysicalPath);
                await RemoveJournalAsync(operationId, quarantine.OwnedName, cancellationToken);
            }

            throw;
        }
    }

    private async Task<long> CopyFileAsync(
        Guid operationId,
        string sourceId,
        string destinationSourceId,
        PlannedFileOperationEntry entry,
        string destinationLogicalPath,
        FileOperationConflictDecision? decision,
        int completedItems,
        long completedBytesBeforeFile,
        FileOperationProgressTracker tracker,
        IReadOnlyList<FileOperationItemOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var source = await pathSecurity.ResolveAsync(
            sourceId,
            entry.SourceLogicalPath,
            cancellationToken);
        var destination = await ResolveDestinationAsync(
            destinationSourceId,
            destinationLogicalPath,
            cancellationToken);
        var parentLogicalPath = Parent(destinationLogicalPath);
        var parent = await pathSecurity.ResolveAsync(
            destinationSourceId,
            parentLogicalPath,
            cancellationToken);
        var stagingName = OwnedName(operationId, "stage");
        var stagingPath = Path.Combine(parent.PhysicalPath, stagingName);
        await AddJournalAsync(
            operationId,
            destinationSourceId,
            parentLogicalPath,
            stagingName,
            destinationLogicalPath,
            isQuarantine: false,
            cancellationToken);
        QuarantinedEntry? quarantine = null;
        long copiedBytes = 0;
        try
        {
            copiedBytes = await fileSystem.CopyFileAsync(
                source.PhysicalPath,
                stagingPath,
                async (delta, token) =>
                {
                    copiedBytes = checked(copiedBytes + delta);
                    var latest = await repository.GetDocumentAsync(operationId, token);
                    if (latest.CancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }

                    await SaveProgressAsync(
                        operationId,
                        tracker.Report(
                            Path.GetFileName(entry.SourceLogicalPath),
                            completedItems,
                            checked(completedBytesBeforeFile + copiedBytes)),
                        outcomes,
                        token);
                },
                cancellationToken);
            if (entry.Fingerprint.Length is not null &&
                (copiedBytes != entry.Fingerprint.Length.Value ||
                 fileSystem.GetFileLength(stagingPath) != entry.Fingerprint.Length.Value))
            {
                throw new IOException("The staged file length does not match preview.");
            }

            fileSystem.ApplyBasicMetadata(source.PhysicalPath, stagingPath);
            if (fileSystem.Exists(destination.PhysicalPath))
            {
                if (decision != FileOperationConflictDecision.Overwrite)
                {
                    throw new IOException("A file destination conflict was not approved.");
                }

                var current = await inspector.GetRequiredAsync(
                    destinationSourceId,
                    destinationLogicalPath,
                    cancellationToken);
                quarantine = await QuarantineAsync(
                    operationId,
                    destinationSourceId,
                    destinationLogicalPath,
                    current.Type,
                    cancellationToken);
            }

            if (fileSystem.TryMove(stagingPath, destination.PhysicalPath) != MoveAttempt.Moved)
            {
                throw new IOException("A same-directory staging commit crossed filesystems.");
            }

            await RemoveJournalAsync(operationId, stagingName, cancellationToken);
            if (quarantine is not null)
            {
                DeleteOwned(quarantine.PhysicalPath, quarantine.Type);
                await RemoveJournalAsync(operationId, quarantine.OwnedName, cancellationToken);
            }

            return copiedBytes;
        }
        catch
        {
            if (fileSystem.Exists(stagingPath))
            {
                fileSystem.DeleteFile(stagingPath);
            }

            await RemoveJournalAsync(operationId, stagingName, cancellationToken);
            if (quarantine is not null &&
                !fileSystem.Exists(destination.PhysicalPath) &&
                fileSystem.Exists(quarantine.PhysicalPath))
            {
                _ = fileSystem.TryMove(quarantine.PhysicalPath, destination.PhysicalPath);
                await RemoveJournalAsync(operationId, quarantine.OwnedName, cancellationToken);
            }

            throw;
        }
    }

    private async Task RevalidateAsync(
        PersistedFileOperationDocument operation,
        CancellationToken cancellationToken)
    {
        var conflictIds = operation.Plan.Conflicts.Select(conflict => conflict.ConflictId).ToArray();
        var resolutions = operation.Approval.Resolutions;
        if (resolutions.Select(resolution => resolution.ConflictId).Distinct().Count() != resolutions.Count ||
            resolutions.Count != conflictIds.Length ||
            resolutions.Any(resolution => !conflictIds.Contains(resolution.ConflictId)))
        {
            throw new DestinationConflictException();
        }

        foreach (var entry in operation.Plan.Entries)
        {
            var source = await inspector.GetRequiredAsync(
                operation.Plan.SourceId!,
                entry.SourceLogicalPath,
                cancellationToken);
            if (source.Fingerprint != entry.Fingerprint || source.IsSymbolicLink)
            {
                throw new OperationPlanStaleException();
            }

            var destination = await inspector.TryGetAsync(
                operation.Plan.DestinationSourceId!,
                entry.DestinationLogicalPath,
                cancellationToken);
            if (destination?.Fingerprint != entry.DestinationFingerprint)
            {
                throw new OperationPlanStaleException();
            }
        }
    }

    private async Task<ResolvedSourcePath> ResolveDestinationAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken) =>
        await pathSecurity.ResolveChildAsync(
            sourceId,
            Parent(logicalPath),
            Name(logicalPath),
            cancellationToken);

    private async Task<string> FindUniqueDestinationAsync(
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

    private async Task<QuarantinedEntry> QuarantineAsync(
        Guid operationId,
        string sourceId,
        string destinationLogicalPath,
        FileEntryType type,
        CancellationToken cancellationToken)
    {
        var destination = await ResolveDestinationAsync(
            sourceId,
            destinationLogicalPath,
            cancellationToken);
        var parentLogicalPath = Parent(destinationLogicalPath);
        var parent = await pathSecurity.ResolveAsync(sourceId, parentLogicalPath, cancellationToken);
        var ownedName = OwnedName(operationId, "quarantine");
        var ownedPath = Path.Combine(parent.PhysicalPath, ownedName);
        await AddJournalAsync(
            operationId,
            sourceId,
            parentLogicalPath,
            ownedName,
            destinationLogicalPath,
            isQuarantine: true,
            cancellationToken);
        if (fileSystem.TryMove(destination.PhysicalPath, ownedPath) != MoveAttempt.Moved)
        {
            throw new IOException("A same-directory quarantine move crossed filesystems.");
        }

        return new(ownedName, ownedPath, type);
    }

    private async Task AddJournalAsync(
        Guid operationId,
        string sourceId,
        string parentLogicalPath,
        string ownedName,
        string? publicDestinationLogicalPath,
        bool isQuarantine,
        CancellationToken cancellationToken)
    {
        await repository.UpdateAsync(
            operationId,
            document =>
            {
                var entries = document.Journal?.Entries.ToList() ?? [];
                entries.Add(new FileOperationJournalEntry(
                    sourceId,
                    parentLogicalPath,
                    ownedName,
                    publicDestinationLogicalPath,
                    isQuarantine));
                return document with
                {
                    Journal = new FileOperationExecutionJournal(operationId, entries.ToArray()),
                };
            },
            cancellationToken);
    }

    private async Task RemoveJournalAsync(
        Guid operationId,
        string ownedName,
        CancellationToken cancellationToken)
    {
        await repository.UpdateAsync(
            operationId,
            document =>
            {
                var entries = document.Journal?.Entries
                    .Where(entry => !entry.OwnedName.Equals(ownedName, StringComparison.Ordinal))
                    .ToArray() ?? [];
                return document with
                {
                    Journal = entries.Length == 0
                        ? null
                        : new FileOperationExecutionJournal(operationId, entries),
                };
            },
            cancellationToken);
    }

    private async Task SaveProgressAsync(
        Guid operationId,
        FileOperationProgress progress,
        IReadOnlyList<FileOperationItemOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        await repository.UpdateAsync(
            operationId,
            document => document with
            {
                Status = document.Status with
                {
                    Progress = progress,
                    Outcomes = outcomes.ToArray(),
                },
            },
            cancellationToken);
    }

    private async Task<FileOperationStatus> MarkFailedAsync(
        Guid operationId,
        string warning,
        CancellationToken cancellationToken)
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
            cancellationToken);
        return failed.Status;
    }

    private void DeleteOwned(string physicalPath, FileEntryType type)
    {
        if (type == FileEntryType.Directory)
        {
            fileSystem.DeleteDirectory(physicalPath, recursive: true);
        }
        else
        {
            fileSystem.DeleteFile(physicalPath);
        }
    }

    private static DestinationRule? FindRule(
        IReadOnlyList<DestinationRule> rules,
        string sourceLogicalPath) =>
        rules.LastOrDefault(rule => IsSameOrDescendant(rule.SourcePrefix, sourceLogicalPath));

    private static string Remap(string sourcePath, string sourcePrefix, string destinationPrefix)
    {
        var suffix = sourcePath[sourcePrefix.Length..];
        return $"{destinationPrefix}{suffix}";
    }

    private static FileOperationItemOutcome Outcome(
        FileOperationPlan plan,
        PlannedFileOperationEntry entry,
        string destinationLogicalPath,
        FileOperationItemResult result,
        string? errorCode,
        string? detail) => new(
            plan.SourceId!,
            entry.SourceLogicalPath,
            plan.DestinationSourceId,
            destinationLogicalPath,
            result,
            errorCode,
            detail);

    private static bool IsSameOrDescendant(string ancestor, string path) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith($"{ancestor}/", StringComparison.OrdinalIgnoreCase);

    private static string Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0 ? "/" : path[..separator];
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    private static string OwnedName(Guid operationId, string purpose) =>
        $"{ReservedFileOperationPathPolicy.OperationPrefix}{operationId:N}-{purpose}-{Guid.NewGuid():N}";

    private sealed record DestinationRule(
        string SourcePrefix,
        string? DestinationPrefix,
        bool Skip);

    private sealed record QuarantinedEntry(
        string OwnedName,
        string PhysicalPath,
        FileEntryType Type);
}
