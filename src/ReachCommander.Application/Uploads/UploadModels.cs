namespace ReachCommander.Application.Uploads;

public sealed record UploadBatchCommand(string SourceId, string DirectoryPath);

public sealed record UploadFilePart(
    string FileName,
    Stream Content,
    long? DeclaredLength);

public sealed record UploadedFile(
    string Name,
    string RelativePath,
    long Size);

public sealed record UploadBatchResult(
    int UploadedCount,
    long TotalBytes,
    IReadOnlyList<UploadedFile> Files);
