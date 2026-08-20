namespace ReachCommander.Application.Uploads;

public interface IUploadService
{
    ValueTask<UploadBatchResult> UploadAsync(
        UploadBatchCommand command,
        IAsyncEnumerable<UploadFilePart> files,
        CancellationToken cancellationToken);
}
