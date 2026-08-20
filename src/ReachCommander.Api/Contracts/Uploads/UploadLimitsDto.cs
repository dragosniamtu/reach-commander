using ReachCommander.Infrastructure.Uploads;

namespace ReachCommander.Api.Contracts.Uploads;

public sealed record UploadLimitsDto(
    long MaxFileBytes,
    long MaxBatchBytes,
    int MaxFilesPerBatch)
{
    public static UploadLimitsDto FromOptions(UploadOptions options) => new(
        options.MaxFileBytes,
        options.MaxBatchBytes,
        options.MaxFilesPerBatch);
}
