namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed record MediaPreviewFileSnapshot(
    long Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes,
    bool IsSymbolicLink)
{
    public MediaFileFingerprint Fingerprint => new(Length, ModifiedAt, Attributes);
}

internal interface IMediaPreviewFileSystem
{
    MediaPreviewFileSnapshot GetFileSnapshot(string physicalPath);

    IReadOnlyList<string> ListNames(string directoryPhysicalPath);

    Task WriteNewAsync(
        string physicalPath,
        ReadOnlyMemory<byte> contents,
        CancellationToken cancellationToken);

    void MoveFile(string sourcePhysicalPath, string destinationPhysicalPath);

    bool FileExists(string physicalPath);

    void DeleteFile(string physicalPath);

    void FlushDirectory(string directoryPhysicalPath);
}
