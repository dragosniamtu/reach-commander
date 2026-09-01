using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class SubtitleSaveExecutor(
    MediaPreviewSessionStore sessions,
    IPathSecurityService pathSecurity,
    IMediaPreviewFileSystem fileSystem,
    SubtitleSavePlanStore plans,
    DirectoryMutationLock mutationLock,
    IOptions<MediaPreviewOptions> options)
{
    private readonly MediaPreviewOptions _options = options.Value;

    public async ValueTask<SubtitleSaveResult> ExecuteAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        var plan = plans.GetRequired(planId);
        var session = sessions.GetRequired(plan.SessionId);
        if (session.SourceReadOnly)
        {
            throw MediaPreviewException.SubtitleSourceReadOnly();
        }

        await using var lease = await mutationLock.AcquireAsync(
            plan.SourceId,
            plan.DirectoryLogicalPath,
            cancellationToken);
        var resolved = await pathSecurity.ResolveAsync(
            plan.SourceId,
            plan.SubtitleLogicalPath,
            cancellationToken);
        MediaPreviewService.EnsureNoSymbolicLinks(resolved);
        if (!SubtitleSavePlanner.PathsEqual(resolved.PhysicalPath, plan.SubtitlePhysicalPath))
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }

        var current = GetSnapshotOrStale(resolved.PhysicalPath);
        if (current.IsSymbolicLink || current.Fingerprint != plan.OriginalFingerprint)
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }

        EnsureBackupStillFree(plan);
        var stagingPath = Path.Combine(
            plan.DirectoryPhysicalPath,
            $".reachcommander-subtitle-{plan.PlanId:N}.partial");
        if (fileSystem.FileExists(stagingPath))
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }

        var backupMoved = false;
        try
        {
            await fileSystem.WriteNewAsync(
                stagingPath,
                plan.CorrectedBytes,
                cancellationToken);
            fileSystem.MoveFile(plan.SubtitlePhysicalPath, plan.BackupPhysicalPath);
            backupMoved = true;
            fileSystem.MoveFile(stagingPath, plan.SubtitlePhysicalPath);
            fileSystem.FlushDirectory(plan.DirectoryPhysicalPath);
        }
        catch (OperationCanceledException) when (!backupMoved)
        {
            TryDelete(stagingPath);
            throw;
        }
        catch (Exception exception) when (!backupMoved && IsMutationFailure(exception))
        {
            TryDelete(stagingPath);
            throw MediaPreviewException.SubtitleSaveFailed();
        }
        catch (Exception exception) when (backupMoved && IsMutationFailure(exception))
        {
            TryDelete(stagingPath);
            try
            {
                fileSystem.MoveFile(plan.BackupPhysicalPath, plan.SubtitlePhysicalPath);
                fileSystem.FlushDirectory(plan.DirectoryPhysicalPath);
            }
            catch (Exception rollbackException) when (IsMutationFailure(rollbackException))
            {
                plans.Remove(planId);
                throw MediaPreviewException.SubtitleRecoveryRequired();
            }

            throw MediaPreviewException.SubtitleSaveFailed();
        }

        var savedSnapshot = fileSystem.GetFileSnapshot(plan.SubtitlePhysicalPath);
        var savedDocument = new SrtParser(
            checked((int)_options.MaximumSubtitleBytes),
            _options.MaximumSubtitleCues).Parse(plan.CorrectedBytes);
        sessions.Update(
            plan.SessionId,
            currentSession => currentSession with
            {
                Subtitle = new StoredSubtitle(
                    plan.SubtitleLogicalPath,
                    plan.SubtitlePhysicalPath,
                    savedSnapshot.Fingerprint,
                    savedDocument),
            });
        plans.Remove(planId);
        return new SubtitleSaveResult(
            plan.SubtitleLogicalPath,
            plan.BackupLogicalPath,
            RecoveryRequired: false);
    }

    private MediaPreviewFileSnapshot GetSnapshotOrStale(string physicalPath)
    {
        try
        {
            return fileSystem.GetFileSnapshot(physicalPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }
    }

    private void EnsureBackupStillFree(StoredSubtitleSavePlan plan)
    {
        var backupName = Path.GetFileName(plan.BackupPhysicalPath);
        if (fileSystem.FileExists(plan.BackupPhysicalPath) ||
            fileSystem.ListNames(plan.DirectoryPhysicalPath)
                .Contains(backupName, StringComparer.OrdinalIgnoreCase))
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }
    }

    private void TryDelete(string physicalPath)
    {
        try
        {
            if (fileSystem.FileExists(physicalPath))
            {
                fileSystem.DeleteFile(physicalPath);
            }
        }
        catch (Exception exception) when (IsMutationFailure(exception))
        {
        }
    }

    private static bool IsMutationFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;
}
