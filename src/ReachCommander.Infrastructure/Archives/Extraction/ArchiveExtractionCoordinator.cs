using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ReachCommander.Application.Archives;
using ReachCommander.Application.Files;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.Archives.Extraction;

internal sealed class ArchiveExtractionCoordinator(
    IArchivePartResolver partResolver,
    IPathSecurityService pathSecurity,
    IArchiveExtractionRuntimeFileSystem fileSystem,
    DirectoryMutationLock mutationLock,
    IArchiveWorkerClient worker,
    ArchiveExtractionOperationStore operations,
    IOptions<ArchiveOptions> options,
    TimeProvider clock,
    ILogger<ArchiveExtractionCoordinator>? logger = null)
{
    private readonly ArchiveOptions _options = options.Value;

    public async Task RunAsync(
        ArchiveExtractionPlan plan,
        string operationId,
        CancellationToken cancellationToken)
    {
        var startedAt = clock.GetTimestamp();
        var stagingName = $".reachcommander-extract-{operationId}.partial";
        ArchiveStagingIdentity? stagingIdentity = null;
        var enteredFinalization = false;
        try
        {
            var sourceDirectory = ParentLogicalPath(plan.ArchivePath);
            await using var mutationLease = await mutationLock.AcquireManyAsync(
                [
                    new(plan.SourceId, sourceDirectory),
                    new(plan.DestinationSourceId, plan.DestinationPath),
                ],
                cancellationToken);

            var currentParts = await ResolveCurrentPartsAsync(plan, cancellationToken);
            using var sourceHandles = OpenSourceHandles(currentParts);
            currentParts = await ResolveCurrentPartsAsync(plan, cancellationToken);
            var destination = await ResolveDestinationAsync(plan, cancellationToken);
            ValidateDestination(plan, destination.PhysicalPath, excludedName: null);

            var staging = await pathSecurity.ResolveChildAsync(
                plan.DestinationSourceId,
                plan.DestinationPath,
                stagingName,
                cancellationToken);
            stagingIdentity = fileSystem.CreateOwnedStagingDirectory(staging.PhysicalPath);
            await using var writer = new ArchiveStagingWriter(
                plan,
                stagingIdentity,
                fileSystem,
                Options.Create(_options),
                (files, bytes, current) => operations.ReportProgress(
                    operationId,
                    files,
                    bytes,
                    current));
            writer.Prepare();
            operations.MarkExtracting(operationId);

            if (plan.Files.Count > 0)
            {
                await worker.ExtractAsync(
                    currentParts,
                    plan.Files.Select(file => file.WorkerEntryIndex).ToArray(),
                    writer,
                    cancellationToken);
            }

            _ = await ResolveCurrentPartsAsync(plan, cancellationToken);
            destination = await ResolveDestinationAsync(plan, cancellationToken);
            ValidateDestination(plan, destination.PhysicalPath, stagingName);

            cancellationToken.ThrowIfCancellationRequested();
            operations.MarkFinalizing(operationId);
            enteredFinalization = true;
            await FinalizeAsync(
                plan,
                operationId,
                destination,
                stagingIdentity,
                CancellationToken.None);
        }
        catch (ArchiveStagingCreationException exception) when (!enteredFinalization)
        {
            stagingIdentity = exception.Identity;
            if (stagingIdentity is not null && TryCleanup(stagingIdentity))
            {
                operations.MarkFailed(operationId, new ArchiveWorkerFailedException());
                return;
            }

            stagingIdentity?.Dispose();
            operations.MarkRecoveryRequired(
                operationId,
                [RecoveryName(stagingIdentity, stagingName)]);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested && !enteredFinalization)
        {
            if (!TryCleanup(stagingIdentity))
            {
                operations.MarkRecoveryRequired(
                    operationId,
                    [RecoveryName(stagingIdentity, stagingName)]);
                return;
            }

            operations.MarkCancelled(operationId);
        }
        catch (ArchiveException exception) when (!enteredFinalization)
        {
            if (!TryCleanup(stagingIdentity))
            {
                operations.MarkRecoveryRequired(
                    operationId,
                    [RecoveryName(stagingIdentity, stagingName)]);
                return;
            }

            operations.MarkFailed(operationId, exception);
        }
        catch (Exception) when (!enteredFinalization)
        {
            if (!TryCleanup(stagingIdentity))
            {
                operations.MarkRecoveryRequired(
                    operationId,
                    [RecoveryName(stagingIdentity, stagingName)]);
                return;
            }

            operations.MarkFailed(operationId, new ArchiveWorkerFailedException());
        }
        finally
        {
            var result = operations.GetRequired(operationId);
            logger?.LogInformation(
                "Archive extraction {OperationId} for source {SourceId} archive {ArchivePath} to {DestinationSourceId}:{DestinationPath} ended {State} after {ElapsedMilliseconds} ms with {CompletedFiles}/{TotalFiles} files and code {ErrorCode}.",
                operationId,
                plan.SourceId,
                plan.ArchivePath,
                plan.DestinationSourceId,
                plan.DestinationPath,
                result.State,
                clock.GetElapsedTime(startedAt).TotalMilliseconds,
                result.CompletedFiles,
                result.TotalFiles,
                result.ErrorCode);
        }
    }

    private async Task FinalizeAsync(
        ArchiveExtractionPlan plan,
        string operationId,
        ResolvedSourcePath destination,
        ArchiveStagingIdentity stagingIdentity,
        CancellationToken cancellationToken)
    {
        var topLevelRoots = plan.SelectedRoots
            .Select(TopLevelComponent)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var moved = new List<(string Name, string Staged, string Final)>();
        try
        {
            foreach (var name in topLevelRoots)
            {
                var final = await pathSecurity.ResolveChildAsync(
                    plan.DestinationSourceId,
                    plan.DestinationPath,
                    name,
                    cancellationToken);
                fileSystem.VerifyTreeHasNoLinks(stagingIdentity);
                var staged = Path.GetFullPath(Path.Combine(stagingIdentity.RootPath, name));
                EnsureWithin(stagingIdentity.RootPath, staged);
                fileSystem.MoveNew(staged, final.PhysicalPath);
                moved.Add((name, staged, final.PhysicalPath));
            }

            fileSystem.DeleteOwnedDirectoryTree(stagingIdentity);
            operations.MarkCompleted(operationId);
        }
        catch
        {
            var uncompensated = new List<string>();
            foreach (var entry in moved.AsEnumerable().Reverse())
            {
                try
                {
                    if (!fileSystem.VerifyOwnedStaging(stagingIdentity))
                    {
                        throw new IOException("Staging ownership changed.");
                    }

                    fileSystem.MoveNew(entry.Final, entry.Staged);
                }
                catch
                {
                    uncompensated.Add(entry.Name);
                }
            }

            if (uncompensated.Count > 0)
            {
                stagingIdentity.Dispose();
                operations.MarkRecoveryRequired(operationId, uncompensated);
                return;
            }

            if (!TryCleanup(stagingIdentity))
            {
                stagingIdentity.Dispose();
                operations.MarkRecoveryRequired(
                    operationId,
                    [Path.GetFileName(stagingIdentity.RecoveryPath)]);
                return;
            }

            operations.MarkFailed(
                operationId,
                new ArchiveDestinationChangedException(),
                ArchiveCompensationState.Succeeded);
        }
    }

    private async ValueTask<ResolvedArchivePartSet> ResolveCurrentPartsAsync(
        ArchiveExtractionPlan plan,
        CancellationToken cancellationToken)
    {
        var current = await partResolver.ResolveAsync(
            plan.SourceId,
            plan.ArchivePath,
            cancellationToken);
        if (!current.Fingerprint.Equals(plan.PartSet.Fingerprint) ||
            current.Parts.Count != plan.PartSet.Parts.Count)
        {
            throw new ArchivePlanStaleException();
        }

        return current;
    }

    private async ValueTask<ResolvedSourcePath> ResolveDestinationAsync(
        ArchiveExtractionPlan plan,
        CancellationToken cancellationToken)
    {
        ResolvedSourcePath destination;
        try
        {
            destination = await pathSecurity.ResolveAsync(
                plan.DestinationSourceId,
                plan.DestinationPath,
                cancellationToken);
        }
        catch (FileAccessException)
        {
            throw new ArchiveDestinationChangedException();
        }

        if (destination.Source.IsReadOnly ||
            !fileSystem.DirectoryExists(destination.PhysicalPath) ||
            !fileSystem.IsRealDirectory(destination.PhysicalPath))
        {
            throw new ArchiveDestinationChangedException();
        }

        return destination;
    }

    private void ValidateDestination(
        ArchiveExtractionPlan plan,
        string physicalDirectory,
        string? excludedName)
    {
        IReadOnlyList<ArchiveDestinationEntry> entries;
        long? freeSpace;
        try
        {
            entries = fileSystem.ListChildren(physicalDirectory)
                .Where(entry => excludedName is null ||
                    !entry.Name.Equals(excludedName, StringComparison.Ordinal))
                .ToArray();
            freeSpace = fileSystem.GetAvailableFreeSpace(physicalDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new ArchiveDestinationChangedException();
        }

        var snapshot = ArchiveExtractionPlanner.CreateDestinationSnapshot(entries);
        if (!snapshot.Equals(plan.DestinationSnapshot, StringComparison.Ordinal))
        {
            throw new ArchiveDestinationChangedException();
        }

        var totalSize = SumKnownSizes(plan.Files);
        if (excludedName is null &&
            totalSize is not null &&
            freeSpace is not null &&
            totalSize > freeSpace)
        {
            throw new ArchiveDestinationChangedException();
        }
    }

    private SourceHandleCollection OpenSourceHandles(ResolvedArchivePartSet partSet)
    {
        var handles = new List<IDisposable>(partSet.Parts.Count);
        try
        {
            foreach (var part in partSet.Parts)
            {
                handles.Add(fileSystem.OpenReadShared(part.PhysicalPath));
            }

            return new SourceHandleCollection(handles);
        }
        catch
        {
            foreach (var handle in handles.AsEnumerable().Reverse())
            {
                handle.Dispose();
            }

            throw new ArchivePlanStaleException();
        }
    }

    private sealed class SourceHandleCollection(IReadOnlyList<IDisposable> handles) : IDisposable
    {
        private IReadOnlyList<IDisposable>? _handles = handles;

        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _handles, null);
            if (owned is null)
            {
                return;
            }

            foreach (var handle in owned.Reverse())
            {
                try
                {
                    handle.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private bool TryCleanup(ArchiveStagingIdentity? stagingIdentity)
    {
        if (stagingIdentity is null)
        {
            return true;
        }

        try
        {
            fileSystem.DeleteOwnedDirectoryTree(stagingIdentity);
            return !fileSystem.DirectoryExists(stagingIdentity.RootPath);
        }
        catch
        {
            stagingIdentity.Dispose();
            return false;
        }
    }

    private static string RecoveryName(
        ArchiveStagingIdentity? stagingIdentity,
        string fallback) =>
        stagingIdentity is null
            ? fallback
            : Path.GetFileName(stagingIdentity.RecoveryPath);

    private static long? SumKnownSizes(IReadOnlyList<PlannedArchiveFile> files)
    {
        long total = 0;
        foreach (var file in files)
        {
            if (file.DeclaredSize is null)
            {
                return null;
            }

            try
            {
                total = checked(total + file.DeclaredSize.Value);
            }
            catch (OverflowException)
            {
                return long.MaxValue;
            }
        }

        return total;
    }

    private static string ParentLogicalPath(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return separator <= 0 ? "/" : logicalPath[..separator];
    }

    private static string TopLevelComponent(string relativePath)
    {
        var separator = relativePath.IndexOf('/');
        return separator < 0 ? relativePath : relativePath[..separator];
    }

    private static void EnsureWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArchiveEntryUnsafeException();
        }
    }
}
