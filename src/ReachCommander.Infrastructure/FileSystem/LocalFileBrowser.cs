using ReachCommander.Application.Files;
using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.Archives.Classification;
using ReachCommander.Infrastructure.FileOperations;

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
            var directory = new DirectoryInfo(resolved.PhysicalPath);
            var fileSystemEntries = directory
                .EnumerateFileSystemInfos()
                .Where(entry => !ReservedFileOperationPathPolicy.IsReservedName(entry.Name))
                .ToArray();
            var siblingNames = fileSystemEntries
                .Select(entry => entry.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var entries = new List<FileEntry>(fileSystemEntries.Length);
            foreach (var entry in fileSystemEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(MapEntry(
                    entry,
                    resolved.LogicalPath,
                    resolved.Source.IsReadOnly,
                    siblingNames));
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

        var parentDirectory = resolved.LogicalPath == "/"
            ? null
            : entry switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            };
        var siblingNames = parentDirectory is null
            ? null
            : parentDirectory.EnumerateFileSystemInfos()
                .Select(candidate => candidate.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = MapEntry(
            entry,
            ParentPath(resolved.LogicalPath),
            resolved.Source.IsReadOnly,
            siblingNames);
        return resolved.LogicalPath == "/"
            ? result with { Name = resolved.Source.Name, RelativePath = "/" }
            : result with { RelativePath = resolved.LogicalPath };
    }

    private static FileEntry MapEntry(
        FileSystemInfo entry,
        string parentLogicalPath,
        bool sourceIsReadOnly,
        IReadOnlySet<string>? siblingNames)
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
        var isSymbolicLink = entry.LinkTarget is not null ||
            attributes.HasFlag(FileAttributes.ReparsePoint);
        var archive = type == FileEntryType.File
            ? ArchiveFilenameClassifier.Classify(entry.Name, isSymbolicLink, siblingNames)
            : null;

        return new FileEntry(
            entry.Name,
            JoinLogicalPath(parentLogicalPath, entry.Name),
            type,
            size,
            new DateTimeOffset(entry.LastWriteTimeUtc),
            extension,
            sourceIsReadOnly || attributes.HasFlag(FileAttributes.ReadOnly),
            isSymbolicLink,
            attributes.ToString(),
            archive?.Format,
            archive?.Role);
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
