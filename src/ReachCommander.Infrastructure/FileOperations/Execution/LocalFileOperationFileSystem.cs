namespace ReachCommander.Infrastructure.FileOperations.Execution;

internal sealed class LocalFileOperationFileSystem : IFileOperationFileSystem
{
    private const int BufferSize = 1024 * 1024;

    public bool Exists(string physicalPath) =>
        File.Exists(physicalPath) || Directory.Exists(physicalPath);

    public void CreateDirectory(string physicalPath) =>
        Directory.CreateDirectory(physicalPath);

    public long GetFileLength(string physicalPath) =>
        new FileInfo(physicalPath).Length;

    public async Task<long> CopyFileAsync(
        string sourcePhysicalPath,
        string destinationPhysicalPath,
        Func<long, CancellationToken, ValueTask> onBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePhysicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPhysicalPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total = checked(total + read);
            await onBytes(read, cancellationToken);
        }

        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return total;
    }

    public MoveAttempt TryMove(string sourcePhysicalPath, string destinationPhysicalPath)
    {
        try
        {
            if (Directory.Exists(sourcePhysicalPath))
            {
                Directory.Move(sourcePhysicalPath, destinationPhysicalPath);
            }
            else
            {
                File.Move(sourcePhysicalPath, destinationPhysicalPath, overwrite: false);
            }
        }
        catch (IOException exception) when (NativeMoveErrorClassifier.IsCrossDevice(exception))
        {
            return MoveAttempt.CrossDevice;
        }

        return MoveAttempt.Moved;
    }

    public void DeleteFile(string physicalPath) => File.Delete(physicalPath);

    public void DeleteDirectory(string physicalPath, bool recursive) =>
        Directory.Delete(physicalPath, recursive);

    public void ApplyBasicMetadata(string sourcePhysicalPath, string destinationPhysicalPath)
    {
        var source = new FileInfo(sourcePhysicalPath);
        source.Refresh();
        File.SetLastWriteTimeUtc(destinationPhysicalPath, source.LastWriteTimeUtc);
        var supported = source.Attributes &
            (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        File.SetAttributes(destinationPhysicalPath, supported);
    }

    public long? GetAvailableBytes(string physicalDirectory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(physicalDirectory));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    public FileAttributes GetAttributes(string physicalPath) =>
        File.GetAttributes(physicalPath);
}
