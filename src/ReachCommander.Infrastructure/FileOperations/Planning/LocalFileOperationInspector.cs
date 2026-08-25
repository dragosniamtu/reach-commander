using ReachCommander.Application.Files;
using ReachCommander.Application.FileOperations;
using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal sealed class LocalFileOperationInspector(IPathSecurityService pathSecurity)
    : IFileOperationInspector
{
    public async ValueTask<FileOperationEntrySnapshot> GetRequiredAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        var resolvedRoot = await pathSecurity.ResolveAsync(sourceId, "/", cancellationToken);
        var current = resolvedRoot.PhysicalPath;
        var segments = logicalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Snapshot(sourceId, "/", resolvedRoot.PhysicalPath);
        }

        for (var index = 0; index < segments.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = Path.GetFullPath(Path.Combine(current, segments[index]));
            EnsureWithin(resolvedRoot.PhysicalPath, current, logicalPath);
            var snapshot = Snapshot(sourceId, Join(segments, index + 1), current);
            if (snapshot.IsSymbolicLink && index < segments.Length - 1)
            {
                throw new UnsafeSymbolicLinkException();
            }

            if (index < segments.Length - 1 && snapshot.Type != FileEntryType.Directory)
            {
                throw new EntryNotFoundException(sourceId, logicalPath);
            }

            if (index == segments.Length - 1)
            {
                return snapshot;
            }
        }

        throw new EntryNotFoundException(sourceId, logicalPath);
    }

    public async ValueTask<FileOperationEntrySnapshot?> TryGetAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetRequiredAsync(sourceId, logicalPath, cancellationToken);
        }
        catch (Exception exception) when (
            exception is EntryNotFoundException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<FileOperationEntrySnapshot>> ListChildrenAsync(
        string sourceId,
        string logicalDirectory,
        CancellationToken cancellationToken)
    {
        var directory = await GetRequiredAsync(sourceId, logicalDirectory, cancellationToken);
        if (directory.IsSymbolicLink)
        {
            throw new UnsafeSymbolicLinkException();
        }

        if (directory.Type != FileEntryType.Directory)
        {
            throw new InvalidLogicalPathException(logicalDirectory, "the entry is not a directory");
        }

        var resolved = await pathSecurity.ResolveAsync(sourceId, logicalDirectory, cancellationToken);
        var children = new List<FileOperationEntrySnapshot>();
        foreach (var child in new DirectoryInfo(resolved.PhysicalPath)
                     .EnumerateFileSystemInfos()
                     .Where(entry => !ReservedFileOperationPathPolicy.IsReservedName(entry.Name))
                     .OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childLogicalPath = logicalDirectory == "/"
                ? $"/{child.Name}"
                : $"{logicalDirectory}/{child.Name}";
            children.Add(Snapshot(sourceId, childLogicalPath, child.FullName));
        }

        return children.AsReadOnly();
    }

    public async ValueTask<long?> GetAvailableBytesAsync(
        string sourceId,
        string logicalDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await pathSecurity.ResolveAsync(
                sourceId,
                logicalDirectory,
                cancellationToken);
            var root = Path.GetPathRoot(resolved.PhysicalPath);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static FileOperationEntrySnapshot Snapshot(
        string sourceId,
        string logicalPath,
        string physicalPath)
    {
        FileSystemInfo info;
        if (Directory.Exists(physicalPath))
        {
            info = new DirectoryInfo(physicalPath);
        }
        else if (File.Exists(physicalPath))
        {
            info = new FileInfo(physicalPath);
        }
        else
        {
            info = new FileInfo(physicalPath);
            info.Refresh();
            if (!info.Exists && info.LinkTarget is null)
            {
                throw new EntryNotFoundException(sourceId, logicalPath);
            }
        }

        info.Refresh();
        var attributes = info.Attributes;
        var isLink = info.LinkTarget is not null || attributes.HasFlag(FileAttributes.ReparsePoint);
        var type = attributes.HasFlag(FileAttributes.Directory)
            ? FileEntryType.Directory
            : info is FileInfo
                ? FileEntryType.File
                : FileEntryType.Other;
        long? length = info is FileInfo file ? file.Length : null;
        var name = logicalPath == "/" ? "/" : logicalPath[(logicalPath.LastIndexOf('/') + 1)..];
        return new FileOperationEntrySnapshot(
            sourceId,
            logicalPath,
            name,
            new FileOperationEntryFingerprint(
                type,
                length,
                new DateTimeOffset(info.LastWriteTimeUtc),
                attributes,
                isLink));
    }

    private static void EnsureWithin(string root, string candidate, string logicalPath)
    {
        var relative = Path.GetRelativePath(root, candidate);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new PathConfinementException(logicalPath);
        }
    }

    private static string Join(string[] segments, int length) =>
        $"/{string.Join('/', segments.Take(length))}";
}
