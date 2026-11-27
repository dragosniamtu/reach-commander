using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;
using System.Security.Cryptography;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class SubtitleSavePlanner(
    MediaPreviewSessionStore sessions,
    IPathSecurityService pathSecurity,
    IMediaPreviewFileSystem fileSystem,
    SubtitleSavePlanStore plans,
    TimeProvider clock,
    IOptions<MediaPreviewOptions> options)
{
    private readonly long _maximumOffset = options.Value.MaximumOffsetMilliseconds;

    public async ValueTask<SubtitleSavePlan> PlanAsync(
        Guid sessionId,
        long offsetMilliseconds,
        CancellationToken cancellationToken,
        double timeScale = 1)
    {
        if (offsetMilliseconds < -_maximumOffset || offsetMilliseconds > _maximumOffset ||
            !double.IsFinite(timeScale) || timeScale is < 0.8 or > 1.25)
        {
            throw MediaPreviewException.SubtitleOffsetInvalid();
        }

        var session = sessions.GetRequired(sessionId);
        if (session.SourceReadOnly)
        {
            throw MediaPreviewException.SubtitleSourceReadOnly();
        }

        var subtitle = session.Subtitle ?? throw MediaPreviewException.SubtitleMissing();
        if (subtitle.Encoding?.RequiresReview == true)
            throw new MediaPreviewException("subtitle_encoding_review_required", "Review the decoded subtitle text before continuing.");
        if (offsetMilliseconds == 0 && timeScale == 1 && subtitle.Encoding?.ConversionNeeded != true)
            throw MediaPreviewException.SubtitleOffsetInvalid();
        var video = await pathSecurity.ResolveAsync(session.SourceId, session.VideoLogicalPath, cancellationToken);
        MediaPreviewService.EnsureNoSymbolicLinks(video);
        if (!PathsEqual(video.PhysicalPath, session.VideoPhysicalPath) ||
            MediaPreviewService.CaptureFingerprint(video.PhysicalPath) != session.VideoFingerprint)
            throw MediaPreviewException.SessionStale();
        var resolved = await pathSecurity.ResolveAsync(
            session.SourceId,
            subtitle.LogicalPath,
            cancellationToken);
        MediaPreviewService.EnsureNoSymbolicLinks(resolved);
        if (!PathsEqual(resolved.PhysicalPath, subtitle.PhysicalPath))
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }

        var snapshot = GetSnapshotOrStale(resolved.PhysicalPath);
        if (resolved.Source.IsReadOnly || snapshot.IsSymbolicLink || snapshot.Fingerprint != subtitle.Fingerprint ||
            (subtitle.ContentHash is not null && subtitle.ContentHash != Convert.ToHexString(SHA256.HashData(
                await MediaPreviewService.ReadBoundedSubtitleAsync(resolved.PhysicalPath, options.Value.MaximumSubtitleBytes, cancellationToken)))))
        {
            throw MediaPreviewException.SubtitleSavePlanStale();
        }

        var directoryPhysicalPath = Path.GetDirectoryName(resolved.PhysicalPath)
            ?? throw MediaPreviewException.SubtitleSavePlanStale();
        var directoryLogicalPath = GetLogicalDirectory(resolved.LogicalPath);
        var backupName = FindBackupName(
            Path.GetFileNameWithoutExtension(resolved.LogicalPath),
            Path.GetExtension(resolved.LogicalPath),
            fileSystem.ListNames(directoryPhysicalPath));
        var backupLogicalPath = directoryLogicalPath == "/"
            ? $"/{backupName}"
            : $"{directoryLogicalPath}/{backupName}";
        var correctedBytes = subtitle.Document.RenderWithTiming(offsetMilliseconds, timeScale);
        if (correctedBytes.Length > options.Value.MaximumSubtitleBytes) throw MediaPreviewException.SubtitleTooLarge();
        var createdAt = clock.GetUtcNow();
        var stored = new StoredSubtitleSavePlan(
            Guid.NewGuid(),
            sessionId,
            createdAt,
            plans.ExpiresAt(createdAt),
            session.SourceId,
            directoryLogicalPath,
            directoryPhysicalPath,
            resolved.LogicalPath,
            resolved.PhysicalPath,
            backupLogicalPath,
            Path.Combine(directoryPhysicalPath, backupName),
            snapshot.Fingerprint,
            correctedBytes,
            offsetMilliseconds,
            timeScale,
            subtitle.ContentHash);
        plans.Add(stored);
        return Map(stored);
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

    private static string FindBackupName(
        string baseName,
        string extension,
        IReadOnlyList<string> existingNames)
    {
        var occupied = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        for (var number = 1; number <= 999; number++)
        {
            var suffix = number == 1 ? string.Empty : $" ({number})";
            var candidate = $"{baseName}_original{suffix}{extension}";
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
        }

        throw MediaPreviewException.SubtitleBackupUnavailable();
    }

    private static SubtitleSavePlan Map(StoredSubtitleSavePlan plan) => new(
        plan.PlanId,
        plan.ExpiresAt,
        plan.SubtitleLogicalPath,
        plan.BackupLogicalPath,
        plan.OffsetMilliseconds,
        CanExecute: true,
        TimeScale: plan.TimeScale);

    private static string GetLogicalDirectory(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return separator <= 0 ? "/" : logicalPath[..separator];
    }

    internal static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal);
}
