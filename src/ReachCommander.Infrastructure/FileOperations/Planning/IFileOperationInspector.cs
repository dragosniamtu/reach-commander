namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal interface IFileOperationInspector
{
    ValueTask<FileOperationEntrySnapshot> GetRequiredAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken);

    ValueTask<FileOperationEntrySnapshot?> TryGetAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<FileOperationEntrySnapshot>> ListChildrenAsync(
        string sourceId,
        string logicalDirectory,
        CancellationToken cancellationToken);

    ValueTask<long?> GetAvailableBytesAsync(
        string sourceId,
        string logicalDirectory,
        CancellationToken cancellationToken);
}
