namespace ReachCommander.Infrastructure.Uploads;

internal sealed record UploadDirectoryEntry(string Name, bool IsSymbolicLink);

internal interface IUploadFileSystem
{
    Stream CreateNewFile(string physicalPath);

    IReadOnlyList<UploadDirectoryEntry> EnumerateDirectory(string physicalDirectory);

    bool DirectoryExists(string physicalDirectory);

    bool FileExists(string physicalPath);

    void MoveWithoutOverwrite(string sourcePhysicalPath, string destinationPhysicalPath);

    void DeleteFileIfExists(string physicalPath);

    long? GetAvailableBytes(string physicalDirectory);
}

internal sealed class LocalUploadFileSystem : IUploadFileSystem
{
    private const int StreamBufferSize = 80 * 1024;

    public Stream CreateNewFile(string physicalPath) => new FileStream(
        physicalPath,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        StreamBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public IReadOnlyList<UploadDirectoryEntry> EnumerateDirectory(string physicalDirectory) =>
        new DirectoryInfo(physicalDirectory)
            .EnumerateFileSystemInfos()
            .Select(entry => new UploadDirectoryEntry(
                entry.Name,
                entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            .ToArray();

    public bool DirectoryExists(string physicalDirectory) => Directory.Exists(physicalDirectory);

    public bool FileExists(string physicalPath) => File.Exists(physicalPath);

    public void MoveWithoutOverwrite(string sourcePhysicalPath, string destinationPhysicalPath) =>
        File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);

    public void DeleteFileIfExists(string physicalPath)
    {
        if (File.Exists(physicalPath))
        {
            File.Delete(physicalPath);
        }
    }

    public long? GetAvailableBytes(string physicalDirectory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(physicalDirectory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
