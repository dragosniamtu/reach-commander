using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Application.TextEncodings;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.TextEncodings;

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
        if (resolved.Source.IsReadOnly) throw MediaPreviewException.SubtitleSourceReadOnly();
        var video = await pathSecurity.ResolveAsync(session.SourceId, session.VideoLogicalPath, cancellationToken);
        MediaPreviewService.EnsureNoSymbolicLinks(video);
        if (!SubtitleSavePlanner.PathsEqual(video.PhysicalPath, session.VideoPhysicalPath) ||
            MediaPreviewService.CaptureFingerprint(video.PhysicalPath) != session.VideoFingerprint)
            throw MediaPreviewException.SessionStale();
        if (!SubtitleSavePlanner.PathsEqual(resolved.PhysicalPath, plan.SubtitlePhysicalPath))
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }

        var current = GetSnapshotOrStale(resolved.PhysicalPath);
        if (current.IsSymbolicLink || current.Fingerprint != plan.OriginalFingerprint ||
            (plan.OriginalHash is not null && plan.OriginalHash != Convert.ToHexString(SHA256.HashData(
                await MediaPreviewService.ReadBoundedSubtitleAsync(resolved.PhysicalPath, _options.MaximumSubtitleBytes, cancellationToken)))))
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

        var savedAnalysis = TextEncodingCodec.Analyze(plan.CorrectedBytes, TextEncodingKind.Utf8, TextEncodingKind.Utf8);
        var savedDocument = SrtDocument.Parse(savedAnalysis.Text, _options.MaximumSubtitleCues);
        var savedHash = Convert.ToHexString(SHA256.HashData(plan.CorrectedBytes));
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
        sessions.Update(
            plan.SessionId,
            currentSession => currentSession with
            {
                Subtitle = new StoredSubtitle(
                    plan.SubtitleLogicalPath,
                    plan.SubtitlePhysicalPath,
                    savedSnapshot.Fingerprint,
                    savedDocument,
                    new SubtitleEncodingInfo(savedAnalysis.SourceEncoding, RequiresReview: false,
                        ConversionNeeded: false, PreviewText: savedAnalysis.PreviewText),
                    savedHash),
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
