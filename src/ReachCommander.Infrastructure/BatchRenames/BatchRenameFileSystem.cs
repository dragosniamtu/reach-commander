using ReachCommander.Application.BatchRenames;
using ReachCommander.Domain.Files;

namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed record EntryFingerprint(
    FileEntryType Type,
    long? Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes);

internal sealed record BatchRenameEntrySnapshot(
    string LogicalPath,
    string PhysicalPath,
    string Name,
    string? Extension,
    FileEntryType Type,
    long? Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes,
    bool IsSymbolicLink)
{
    public EntryFingerprint Fingerprint => new(Type, Length, ModifiedAt, Attributes);
}

internal interface IBatchRenameFileSystem
{
    BatchRenameEntrySnapshot GetEntry(string logicalPath, string physicalPath);

    IReadOnlyList<BatchRenameEntrySnapshot> ListChildren(
        string parentLogicalPath,
        string parentPhysicalPath);

    bool EntryExists(string physicalPath);

    void Move(string sourcePhysicalPath, string destinationPhysicalPath, FileEntryType type);
}

internal sealed class LocalBatchRenameFileSystem : IBatchRenameFileSystem
{
    public BatchRenameEntrySnapshot GetEntry(string logicalPath, string physicalPath)
    {
        try
        {
            return MapEntry(logicalPath, physicalPath);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RenamePlanStaleException("A selected entry no longer exists.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new RenamePlanStaleException("A selected entry is no longer accessible.");
        }
    }

    public IReadOnlyList<BatchRenameEntrySnapshot> ListChildren(
        string parentLogicalPath,
        string parentPhysicalPath)
    {
        try
        {
            return new DirectoryInfo(parentPhysicalPath)
                .EnumerateFileSystemInfos()
                .Select(entry => MapEntry(
                    JoinLogicalPath(parentLogicalPath, entry.Name),
                    entry.FullName,
                    entry))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new RenamePlanStaleException("The selected directory no longer exists.");
        }
        catch (UnauthorizedAccessException)
        {
            throw new RenamePlanStaleException("The selected directory is no longer accessible.");
        }
    }

    public bool EntryExists(string physicalPath)
    {
        try
        {
            _ = File.GetAttributes(physicalPath);
            return true;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    public void Move(string sourcePhysicalPath, string destinationPhysicalPath, FileEntryType type)
    {
        switch (type)
        {
            case FileEntryType.File:
                File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);
                break;
            case FileEntryType.Directory:
                Directory.Move(sourcePhysicalPath, destinationPhysicalPath);
                break;
            default:
                throw new RenamePlanStaleException("Only files and directories can be renamed.");
        }
    }

    private static BatchRenameEntrySnapshot MapEntry(
        string logicalPath,
        string physicalPath,
        FileSystemInfo? knownEntry = null)
    {
        var attributes = File.GetAttributes(physicalPath);
        var type = attributes.HasFlag(FileAttributes.Directory)
            ? FileEntryType.Directory
            : FileEntryType.File;
        FileSystemInfo entry = knownEntry ?? (type == FileEntryType.Directory
            ? new DirectoryInfo(physicalPath)
            : new FileInfo(physicalPath));
        var extension = type == FileEntryType.File ? GetExtension(entry.Name) : null;
        long? length = type == FileEntryType.File ? ((FileInfo)entry).Length : null;

        return new BatchRenameEntrySnapshot(
            logicalPath,
            Path.GetFullPath(physicalPath),
            entry.Name,
            extension,
            type,
            length,
            new DateTimeOffset(entry.LastWriteTimeUtc),
            attributes,
            entry.LinkTarget is not null || attributes.HasFlag(FileAttributes.ReparsePoint));
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
}
