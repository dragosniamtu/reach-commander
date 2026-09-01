namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class LocalMediaPreviewFileSystem : IMediaPreviewFileSystem
{
    public MediaPreviewFileSnapshot GetFileSnapshot(string physicalPath)
    {
        var file = new FileInfo(physicalPath);
        file.Refresh();
        if (!file.Exists)
        {
            throw new FileNotFoundException("The media file no longer exists.", physicalPath);
        }

        return new MediaPreviewFileSnapshot(
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc),
            file.Attributes,
            file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    public IReadOnlyList<string> ListNames(string directoryPhysicalPath) =>
        new DirectoryInfo(directoryPhysicalPath)
            .EnumerateFileSystemInfos()
            .Select(entry => entry.Name)
            .ToArray();

    public async Task WriteNewAsync(
        string physicalPath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            physicalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(contents, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    public void MoveFile(string sourcePhysicalPath, string destinationPhysicalPath) =>
        File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);

    public bool FileExists(string physicalPath) => File.Exists(physicalPath);

    public void DeleteFile(string physicalPath) => File.Delete(physicalPath);

    public void FlushDirectory(string directoryPhysicalPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var handle = File.OpenHandle(
                directoryPhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
