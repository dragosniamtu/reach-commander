using Microsoft.Extensions.Options;
using System.Text.Json;
using ReachCommander.Application.Files;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.FileOperations;
using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed class SubtitleDiscovery(IPathSecurityService paths, IOptions<SubtitleGenerationOptions> options)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm" };

    internal async Task<ResolvedSourcePath> ValidateSelectionAsync(string sourceId, string path, CancellationToken token)
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
        var root = await ValidateSelectionAsync(request.SourceId, request.Path, token);
        if (File.Exists(root.PhysicalPath)) return [new(root.LogicalPath, SubtitleFilePhase.Pending)];
        var found = new List<SubtitleFileStatus>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var pending = new Stack<ResolvedSourcePath>();
        pending.Push(root);
        var directories = 1;
        var metadataBytes = 0;
        void AddFile(SubtitleFileStatus file)
        {
            // Reserve space for terminal error/status fields, using the same escaping as persisted JSON.
            metadataBytes = checked(metadataBytes + JsonSerializer.SerializeToUtf8Bytes(file.Path).Length + 512);
            if (found.Count >= options.Value.MaximumFiles || metadataBytes > 4 * 1024 * 1024)
                throw new SubtitleGenerationException("subtitle_selection_too_large", "The folder contains too many video paths. Select a smaller folder.");
            found.Add(file);
        }
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                directory = await ValidateSelectionAsync(request.SourceId, directory.LogicalPath, token);
                foreach (var physical in Directory.EnumerateFileSystemEntries(directory.PhysicalPath))
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(physical);
                    if (ReservedFileOperationPathPolicy.IsReservedName(name)) continue;
                    var logical = directory.LogicalPath == "/" ? $"/{name}" : $"{directory.LogicalPath}/{name}";
                    if (!Directory.Exists(physical) && !IsVideo(physical)) continue;
                    try
                    {
                        var child = await ValidateSelectionAsync(request.SourceId, logical, token);
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

    internal async Task<SubtitleVideo> ResolveVideoAsync(string sourceId, string path, CancellationToken token)
    {
        var resolved = await ValidateSelectionAsync(sourceId, path, token);
        if (!File.Exists(resolved.PhysicalPath) || !IsVideo(resolved.PhysicalPath))
            throw new SubtitleGenerationException("subtitle_video_changed", "The selected video is no longer available.");
        var file = new FileInfo(resolved.PhysicalPath);
        var directory = file.DirectoryName!;
        if (new DriveInfo(directory).AvailableFreeSpace < options.Value.MinimumFreeBytes)
            throw new SubtitleGenerationException("subtitle_insufficient_space", "There is insufficient free space beside the video.");
        // CreateNew + DeleteOnClose proves write permission without touching media or existing sidecars.
        var probe = Path.Combine(directory, $".reachcommander-subtitle-probe-{Guid.NewGuid():N}.partial");
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        var slash = resolved.LogicalPath.LastIndexOf('/');
        return new(sourceId, resolved.LogicalPath, resolved.PhysicalPath,
            slash == 0 ? "/" : resolved.LogicalPath[..slash], file.Length, file.LastWriteTimeUtc.Ticks);
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
