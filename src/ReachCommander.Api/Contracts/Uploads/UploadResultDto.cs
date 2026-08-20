using ReachCommander.Application.Uploads;

namespace ReachCommander.Api.Contracts.Uploads;

public sealed record UploadedFileDto(string Name, string RelativePath, long Size)
{
    public static UploadedFileDto FromResult(UploadedFile file) =>
        new(file.Name, file.RelativePath, file.Size);
}

public sealed record UploadResultDto(
    int UploadedCount,
    long TotalBytes,
    IReadOnlyList<UploadedFileDto> Files)
{
    public static UploadResultDto FromResult(UploadBatchResult result) => new(
        result.UploadedCount,
        result.TotalBytes,
        result.Files.Select(UploadedFileDto.FromResult).ToArray());
}
