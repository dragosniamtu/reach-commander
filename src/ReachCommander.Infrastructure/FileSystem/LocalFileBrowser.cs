using ReachCommander.Application.Files;
using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.FileSystem;

public sealed class LocalFileBrowser(IPathSecurityService pathSecurity) : IFileBrowser
{
    public async ValueTask<IReadOnlyList<FileEntry>> ListAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        var resolved = await pathSecurity.ResolveAsync(sourceId, logicalPath, cancellationToken);
        if (!Directory.Exists(resolved.PhysicalPath))
        {
            throw new InvalidLogicalPathException(
                resolved.LogicalPath,
                "the selected entry is not a directory");
        }

        try
        {
            var entries = new List<FileEntry>();
            var directory = new DirectoryInfo(resolved.PhysicalPath);
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(MapEntry(entry, resolved.LogicalPath, resolved.Source.IsReadOnly));
            }

            return entries.AsReadOnly();
        }
        catch (DirectoryNotFoundException)
        {
            throw new EntryNotFoundException(sourceId, resolved.LogicalPath);
        }
        catch (UnauthorizedAccessException)
        {
            throw new SourceUnavailableException(sourceId);
        }
    }

    public async ValueTask<FileEntry> GetInfoAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken)
    {
        var resolved = await pathSecurity.ResolveAsync(sourceId, logicalPath, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        FileSystemInfo entry = Directory.Exists(resolved.PhysicalPath)
            ? new DirectoryInfo(resolved.PhysicalPath)
            : new FileInfo(resolved.PhysicalPath);

        var result = MapEntry(entry, ParentPath(resolved.LogicalPath), resolved.Source.IsReadOnly);
        return resolved.LogicalPath == "/"
            ? result with { Name = resolved.Source.Name, RelativePath = "/" }
            : result with { RelativePath = resolved.LogicalPath };
    }

    private static FileEntry MapEntry(
        FileSystemInfo entry,
        string parentLogicalPath,
        bool sourceIsReadOnly)
    {
        var attributes = entry.Attributes;
        var type = attributes.HasFlag(FileAttributes.Directory)
            ? FileEntryType.Directory
            : entry is FileInfo
                ? FileEntryType.File
                : FileEntryType.Other;
        var extension = type == FileEntryType.File
            ? GetExtension(entry.Name)
            : null;
        long? size = entry is FileInfo file ? file.Length : null;

        return new FileEntry(
            entry.Name,
            JoinLogicalPath(parentLogicalPath, entry.Name),
            type,
            size,
            new DateTimeOffset(entry.LastWriteTimeUtc),
            extension,
            sourceIsReadOnly || attributes.HasFlag(FileAttributes.ReadOnly),
            entry.LinkTarget is not null || attributes.HasFlag(FileAttributes.ReparsePoint),
            attributes.ToString());
    }

    private static string? GetExtension(string name)
    {
        var extension = Path.GetExtension(name);
        return string.IsNullOrEmpty(extension) || extension.Length == name.Length
            ? null
            : extension[1..];
    }

    private static string JoinLogicalPath(string parent, string name) =>
        parent == "/" ? $"/{name}" : $"{parent}/{name}";

    private static string ParentPath(string logicalPath)
    {
        if (logicalPath == "/")
        {
            return "/";
        }

        var separator = logicalPath.LastIndexOf('/');
        return separator <= 0 ? "/" : logicalPath[..separator];
    }
}
