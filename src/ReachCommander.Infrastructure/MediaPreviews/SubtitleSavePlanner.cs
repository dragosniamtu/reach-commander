using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;

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
        CancellationToken cancellationToken)
    {
        if (offsetMilliseconds == 0 ||
            offsetMilliseconds < -_maximumOffset ||
            offsetMilliseconds > _maximumOffset)
        {
            throw MediaPreviewException.SubtitleOffsetInvalid();
        }

        var session = sessions.GetRequired(sessionId);
        if (session.SourceReadOnly)
        {
            throw MediaPreviewException.SubtitleSourceReadOnly();
        }

        var subtitle = session.Subtitle ?? throw MediaPreviewException.SubtitleMissing();
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
        if (snapshot.IsSymbolicLink || snapshot.Fingerprint != subtitle.Fingerprint)
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
        var correctedBytes = subtitle.Document.RenderWithOffset(offsetMilliseconds);
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
            offsetMilliseconds);
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
        CanExecute: true);

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
