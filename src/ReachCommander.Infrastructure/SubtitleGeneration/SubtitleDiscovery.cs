using Microsoft.Extensions.Options;
using System.Text.Json;
using ReachCommander.Application.Files;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.FileOperations;
using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed class SubtitleDiscovery(IPathSecurityService paths, IOptions<SubtitleGenerationOptions> options)
{
    private const int MaximumBatchPathBytes = 1024 * 1024;
    private const int MaximumFileMetadataBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm" };
    private static readonly StringComparer PhysicalPathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal async Task<(SubtitleGenerationRequest Request, IReadOnlyList<SubtitleVideoSnapshot>? Snapshots)> ValidateSelectionAsync(
        SubtitleGenerationRequest request, CancellationToken token)
    {
        if (request.Paths is not { } selection)
        {
            var resolved = await ResolveSelectionAsync(request.SourceId, request.Path, token);
            return (request with { SourceId = resolved.Source.Id, Path = resolved.LogicalPath }, null);
        }

        if (selection.Count == 0 || !string.Equals(request.Path, selection[0], StringComparison.Ordinal))
            throw new SubtitleGenerationException("subtitle_selection_invalid", "Select at least one video and use its first path as the job path.");
        if (selection.Count > options.Value.MaximumFiles)
            throw BatchTooLarge();

        var selectedPaths = selection.ToArray();
        // Bound the raw list before performing any filesystem work.
        var requestBytes = 2;
        foreach (var path in selectedPaths)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
                throw new SubtitleGenerationException("subtitle_selection_invalid", "Each selected video requires a path.");
            if (path.Length > SubtitleGenerationRequest.MaximumPathLength)
                throw BatchTooLarge();
            requestBytes += JsonSerializer.SerializeToUtf8Bytes(path).Length + 1;
            if (requestBytes > MaximumBatchPathBytes)
                throw BatchTooLarge();
        }

        var seen = new HashSet<string>(PhysicalPathComparer);
        var writableDirectories = new HashSet<string>(PhysicalPathComparer);
        var snapshots = new List<SubtitleVideoSnapshot>();
        var fileMetadataBytes = 0;
        var sourceId = request.SourceId;
        foreach (var path in selectedPaths)
        {
            var file = await ResolveSelectionAsync(request.SourceId, path, token);
            var info = new FileInfo(file.PhysicalPath);
            if (!info.Exists)
                throw new SubtitleGenerationException("subtitle_selection_invalid", "Each batch entry must be an existing video file.");
            EnsureWritableVideo(info);
            if (!seen.Add(file.PhysicalPath)) continue;
            fileMetadataBytes += FileMetadataSize(file.LogicalPath);
            if (fileMetadataBytes > MaximumFileMetadataBytes)
                throw BatchTooLarge();

            var directory = info.DirectoryName!;
            if (writableDirectories.Add(directory))
            {
                try { ProbeWriteAccess(directory); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw new SubtitleGenerationException("subtitle_selection_invalid", "Unable to write subtitles beside every selected video.");
                }
            }
            sourceId = file.Source.Id;
            snapshots.Add(new(file.LogicalPath, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        var normalized = snapshots.Select(file => file.Path).ToArray();
        return (request with { SourceId = sourceId, Path = normalized[0], Paths = normalized }, snapshots.ToArray());
    }

    private async Task<ResolvedSourcePath> ResolveSelectionAsync(string sourceId, string path, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(path))
            throw new SubtitleGenerationException("subtitle_selection_invalid", "Select one video or folder.");
        var resolved = await paths.ResolveAsync(sourceId, path, token);
        EnsureWritableAndNoLinks(resolved);
        if (!Directory.Exists(resolved.PhysicalPath) && !IsVideo(resolved.PhysicalPath))
            throw new SubtitleGenerationException("video_format_unsupported", "Select an MKV, MP4, AVI, MOV, M4V or WebM video, or a folder.");
        return resolved;
    }

    internal async Task<IReadOnlyList<SubtitleFileStatus>> DiscoverAsync(SubtitleGenerationRequest request, CancellationToken token)
    {
        var root = await ResolveSelectionAsync(request.SourceId, request.Path, token);
        if (File.Exists(root.PhysicalPath)) return [new(root.LogicalPath, SubtitleFilePhase.Pending)];
        var found = new List<SubtitleFileStatus>();
        var seen = new HashSet<string>(PhysicalPathComparer);
        var pending = new Stack<ResolvedSourcePath>();
        pending.Push(root);
        var directories = 1;
        var metadataBytes = 0;
        void AddFile(SubtitleFileStatus file)
        {
            metadataBytes = checked(metadataBytes + FileMetadataSize(file.Path));
            if (found.Count >= options.Value.MaximumFiles || metadataBytes > MaximumFileMetadataBytes)
                throw new SubtitleGenerationException("subtitle_selection_too_large", "The folder contains too many video paths. Select a smaller folder.");
            found.Add(file);
        }
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                directory = await ResolveSelectionAsync(request.SourceId, directory.LogicalPath, token);
                foreach (var physical in Directory.EnumerateFileSystemEntries(directory.PhysicalPath))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(physical);
                    if (ReservedFileOperationPathPolicy.IsReservedName(name)) continue;
                    var logical = directory.LogicalPath == "/" ? $"/{name}" : $"{directory.LogicalPath}/{name}";
                    if (!Directory.Exists(physical) && !IsVideo(physical)) continue;
                    try
                    {
                        var child = await ResolveSelectionAsync(request.SourceId, logical, token);
                        if (!seen.Add(child.PhysicalPath)) continue;
                        if (Directory.Exists(child.PhysicalPath))
                        {
                            if (++directories > options.Value.MaximumDirectories)
                                throw new SubtitleGenerationException("subtitle_selection_too_large", "The folder contains too many directories. Select a smaller folder.");
                            pending.Push(child);
                        }
                        else AddFile(new(child.LogicalPath, SubtitleFilePhase.Pending));
                    }
                    catch (Exception exception) when (IsFileFailure(exception))
                    {
                        AddFile(new(logical, SubtitleFilePhase.Failed, "This entry is unavailable or is not safe to process."));
                    }
                }
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                AddFile(new(directory.LogicalPath, SubtitleFilePhase.Failed, "This folder could not be read safely."));
            }
        }
        return found.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray();
    }

    internal async Task<SubtitleVideo> ResolveVideoAsync(string sourceId, string path, bool requireWritableVideo, CancellationToken token)
    {
        var resolved = await ResolveSelectionAsync(sourceId, path, token);
        var file = new FileInfo(resolved.PhysicalPath);
        if (!file.Exists || !IsVideo(resolved.PhysicalPath))
            throw new SubtitleGenerationException("subtitle_video_changed", "The selected video is no longer available.");
        if (requireWritableVideo) EnsureWritableVideo(file);
        var directory = file.DirectoryName!;
        if (new DriveInfo(directory).AvailableFreeSpace < options.Value.MinimumFreeBytes)
            throw new SubtitleGenerationException("subtitle_insufficient_space", "There is insufficient free space beside the video.");
        ProbeWriteAccess(directory);
        var slash = resolved.LogicalPath.LastIndexOf('/');
        return new(sourceId, resolved.LogicalPath, resolved.PhysicalPath,
            slash == 0 ? "/" : resolved.LogicalPath[..slash], file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static SubtitleGenerationException BatchTooLarge() =>
        new("subtitle_selection_too_large", "The video selection exceeds the subtitle job limit. Select fewer or shorter paths.");

    // Reserve space for terminal error/status fields, using the same escaping as persisted JSON.
    private static int FileMetadataSize(string path) => JsonSerializer.SerializeToUtf8Bytes(path).Length + 512;

    private static void ProbeWriteAccess(string directory)
    {
        // CreateNew + DeleteOnClose proves write permission without touching media or existing sidecars.
        var probe = Path.Combine(directory, $".reachcommander-subtitle-probe-{Guid.NewGuid():N}.partial");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    private static void EnsureWritableVideo(FileInfo file)
    {
        if (file.IsReadOnly)
            throw new SubtitleGenerationException("subtitle_selection_invalid", "A selected video is read-only.");
    }

    private static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    private static void EnsureWritableAndNoLinks(ResolvedSourcePath resolved)
    {
        if (resolved.Source.IsReadOnly)
            throw new SubtitleGenerationException("source_read_only", "Subtitle generation requires a writable source.");
        MediaPreviewService.EnsureNoSymbolicLinks(resolved);
    }

    private static bool IsFileFailure(Exception exception) => exception is
        FileAccessException or IOException or UnauthorizedAccessException or Application.MediaPreviews.MediaPreviewException;
}
