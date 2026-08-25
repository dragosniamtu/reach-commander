namespace ReachCommander.Infrastructure.FileOperations.Execution;

internal enum MoveAttempt
{
    Moved,
    CrossDevice,
}

internal interface IFileOperationFileSystem
{
    bool Exists(string physicalPath);

    void CreateDirectory(string physicalPath);

    long GetFileLength(string physicalPath);

    Task<long> CopyFileAsync(
        string sourcePhysicalPath,
        string destinationPhysicalPath,
        Func<long, CancellationToken, ValueTask> onBytes,
        CancellationToken cancellationToken);

    MoveAttempt TryMove(string sourcePhysicalPath, string destinationPhysicalPath);

    void DeleteFile(string physicalPath);

    void DeleteDirectory(string physicalPath, bool recursive);

    void ApplyBasicMetadata(string sourcePhysicalPath, string destinationPhysicalPath);

    long? GetAvailableBytes(string physicalDirectory);

    FileAttributes GetAttributes(string physicalPath);
}
