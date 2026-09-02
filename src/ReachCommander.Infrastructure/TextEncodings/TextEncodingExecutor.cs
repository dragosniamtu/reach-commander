using Microsoft.Extensions.Logging;
using ReachCommander.Application.Files;
using ReachCommander.Application.TextEncodings;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.TextEncodings;

internal interface ITextEncodingExecutor
{
    Task RunAsync(
        StoredTextEncodingPlan plan,
        Guid operationId,
        CancellationToken cancellationToken);
}

internal sealed class TextEncodingExecutor(
    IPathSecurityService pathSecurity,
    ITextEncodingFileSystem fileSystem,
    TextEncodingOperationStore operationStore,
    DirectoryMutationLock mutationLock,
    ILogger<TextEncodingExecutor> logger,
    TextEncodingStagingRegistry? stagingRegistry = null) : ITextEncodingExecutor
{
    public async Task RunAsync(
        StoredTextEncodingPlan plan,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            operationStore.GetCancellationToken(operationId));

        try
        {
            var targets = plan.Entries
                .Select(entry => new DirectoryMutationTarget(plan.SourceId, entry.LogicalDirectory))
                .Distinct()
                .ToArray();
            if (targets.Length == 0)
            {
                operationStore.MarkRunning(operationId);
                operationStore.MarkTerminal(operationId, TextEncodingOperationState.Completed);
                return;
            }

            await using var lease = await mutationLock.AcquireManyAsync(
                targets,
                linkedCancellation.Token);
            operationStore.MarkRunning(operationId);

            for (var index = 0; index < plan.Entries.Count; index++)
            {
                if (linkedCancellation.IsCancellationRequested)
                {
                    operationStore.MarkTerminal(operationId, TextEncodingOperationState.Cancelled);
                    return;
                }

                var entry = plan.Entries[index];
                operationStore.BeginFile(operationId, index, entry.FileName);
                var recoveryRequired = await ExecuteFileAsync(
                    plan.SourceId,
                    operationId,
                    index,
                    entry);
                if (recoveryRequired)
                {
                    break;
                }
            }

            operationStore.MarkTerminal(operationId, TextEncodingOperationState.Completed);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            operationStore.MarkTerminal(operationId, TextEncodingOperationState.Cancelled);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Text encoding operation {OperationId} failed before row-level completion.",
                operationId);
            operationStore.MarkTerminal(
                operationId,
                TextEncodingOperationState.Failed,
                "text_encoding_operation_failed",
                "The encoding operation failed before all files could be processed.");
        }
    }

    private async Task<bool> ExecuteFileAsync(
        string sourceId,
        Guid operationId,
        int rowIndex,
        StoredTextEncodingEntry entry)
    {
        TextFileSnapshot snapshot;
        try
        {
            var resolved = await pathSecurity.ResolveAsync(
                sourceId,
                entry.LogicalPath,
                CancellationToken.None);
            if (!PhysicalPathsEqual(resolved.PhysicalPath, entry.PhysicalPath))
            {
                CompleteSkipped(operationId, rowIndex, "text_file_stale");
                return false;
            }

            snapshot = await fileSystem.ReadSnapshotAsync(
                entry.LogicalPath,
                resolved.PhysicalPath,
                PathTraversedSymbolicLink(resolved),
                CancellationToken.None);
            if (snapshot.IsSymbolicLink)
            {
                CompleteSkipped(operationId, rowIndex, "text_symbolic_link_rejected");
                return false;
            }
        }
        catch (TextEncodingException exception) when (
            exception.Code == "text_symbolic_link_rejected")
        {
            CompleteSkipped(operationId, rowIndex, exception.Code);
            return false;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or FileAccessException or TextEncodingException)
        {
            CompleteSkipped(operationId, rowIndex, "text_file_stale");
            return false;
        }

        if (snapshot.Fingerprint != entry.Fingerprint)
        {
            CompleteSkipped(operationId, rowIndex, "text_file_stale");
            return false;
        }

        byte[] convertedBytes;
        try
        {
            var analysis = TextEncodingCodec.Analyze(
                snapshot.Bytes,
                entry.SourceEncoding,
                entry.OutputEncoding);
            convertedBytes = TextEncodingCodec.Encode(analysis.Text, entry.OutputEncoding);
        }
        catch (TextEncodingException)
        {
            CompleteSkipped(operationId, rowIndex, "text_file_stale");
            return false;
        }

        var backup = AllocateBackup(entry);
        if (backup is null)
        {
            operationStore.CompleteFile(
                operationId,
                rowIndex,
                TextEncodingRowResult.Failed,
                backupPath: null,
                "text_backup_name_exhausted",
                "No free original-backup name is available.");
            return false;
        }

        var stagingName = $".reachcommander-operation-encoding-{operationId:N}-{rowIndex:D3}.partial";
        var stagingPhysicalPath = Path.Combine(entry.PhysicalDirectory, stagingName);
        var backupMoved = false;
        var published = false;
        TextEncodingStagingRecord? stagingRecord = null;
        try
        {
            if (stagingRegistry is not null)
            {
                stagingRecord = await stagingRegistry.RegisterAsync(
                    sourceId,
                    entry.LogicalDirectory,
                    stagingName,
                    CancellationToken.None);
            }

            await fileSystem.WriteNewAsync(
                stagingPhysicalPath,
                convertedBytes,
                CancellationToken.None);
            fileSystem.MoveFile(entry.PhysicalPath, backup.PhysicalPath);
            backupMoved = true;
            fileSystem.MoveFile(stagingPhysicalPath, entry.PhysicalPath);
            published = true;
            fileSystem.FlushDirectory(entry.PhysicalDirectory);
            operationStore.CompleteFile(
                operationId,
                rowIndex,
                TextEncodingRowResult.Converted,
                backup.LogicalPath,
                code: null,
                detail: null);
            logger.LogInformation(
                "Text encoding operation {OperationId} converted {FileName} from {SourceEncoding} to {OutputEncoding} ({ByteCount} source bytes).",
                operationId,
                entry.FileName,
                entry.SourceEncoding,
                entry.OutputEncoding,
                snapshot.Fingerprint.Length);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                "Text encoding operation {OperationId} could not publish {FileName}.",
                operationId,
                entry.FileName);
            return HandleTransactionFailure(
                operationId,
                rowIndex,
                entry,
                backup,
                stagingPhysicalPath,
                backupMoved,
                published);
        }
        finally
        {
            if (stagingRecord is not null && !fileSystem.FileExists(stagingPhysicalPath))
            {
                stagingRegistry!.Remove(stagingRecord.RecordId);
            }
        }
    }

    private bool HandleTransactionFailure(
        Guid operationId,
        int rowIndex,
        StoredTextEncodingEntry entry,
        BackupCandidate backup,
        string stagingPhysicalPath,
        bool backupMoved,
        bool published)
    {
        TryDeleteStaging(stagingPhysicalPath);
        if (!backupMoved)
        {
            CompleteFailed(operationId, rowIndex);
            return false;
        }

        try
        {
            if (published && fileSystem.FileExists(entry.PhysicalPath))
            {
                fileSystem.DeleteFile(entry.PhysicalPath);
            }

            fileSystem.MoveFile(backup.PhysicalPath, entry.PhysicalPath);
            fileSystem.FlushDirectory(entry.PhysicalDirectory);
            CompleteFailed(operationId, rowIndex);
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                "Text encoding operation {OperationId} requires manual recovery for {FileName}.",
                operationId,
                entry.FileName);
            operationStore.CompleteFile(
                operationId,
                rowIndex,
                TextEncodingRowResult.RecoveryRequired,
                backup.LogicalPath,
                "text_encoding_recovery_required",
                $"Restore {backup.LogicalPath} to {entry.LogicalPath} before retrying.");
            return true;
        }
    }

    private BackupCandidate? AllocateBackup(StoredTextEncodingEntry entry)
    {
        var existingNames = new HashSet<string>(
            fileSystem.ListNames(entry.PhysicalDirectory),
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var extension = Path.GetExtension(entry.FileName);
        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        for (var number = 1; number <= 999; number++)
        {
            var suffix = number == 1 ? "_original" : $"_original ({number})";
            var name = $"{stem}{suffix}{extension}";
            var physicalPath = Path.Combine(entry.PhysicalDirectory, name);
            if (existingNames.Contains(name) || fileSystem.FileExists(physicalPath))
            {
                continue;
            }

            var logicalPath = entry.LogicalDirectory == "/"
                ? $"/{name}"
                : $"{entry.LogicalDirectory}/{name}";
            return new BackupCandidate(logicalPath, physicalPath);
        }

        return null;
    }

    private void TryDeleteStaging(string stagingPhysicalPath)
    {
        try
        {
            if (fileSystem.FileExists(stagingPhysicalPath))
            {
                fileSystem.DeleteFile(stagingPhysicalPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void CompleteSkipped(Guid operationId, int rowIndex, string code) =>
        operationStore.CompleteFile(
            operationId,
            rowIndex,
            TextEncodingRowResult.Skipped,
            backupPath: null,
            code,
            code == "text_symbolic_link_rejected"
                ? "The file became a symbolic link after preview and was not changed."
                : "The file changed after preview and was not changed.");

    private void CompleteFailed(Guid operationId, int rowIndex) =>
        operationStore.CompleteFile(
            operationId,
            rowIndex,
            TextEncodingRowResult.Failed,
            backupPath: null,
            "text_conversion_failed",
            "The file could not be converted and its original contents were preserved.");

    private static bool PhysicalPathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathTraversedSymbolicLink(ResolvedSourcePath resolved)
    {
        var relative = resolved.LogicalPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var lexicalPath = Path.GetFullPath(Path.Combine(resolved.Source.RootPath, relative));
        return !PhysicalPathsEqual(lexicalPath, resolved.PhysicalPath);
    }

    private sealed record BackupCandidate(string LogicalPath, string PhysicalPath);
}
