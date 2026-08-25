namespace ReachCommander.Application.FileOperations;

public interface IFileOperationService
{
    Task<FileOperationPreview> PreviewAsync(
        FileOperationPreviewRequest request,
        CancellationToken cancellationToken);

    Task<FileOperationStatus> SubmitAsync(
        FileOperationSubmission request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<FileOperationStatus>> ListAsync(
        CancellationToken cancellationToken);

    Task<FileOperationStatus> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<FileOperationStatus> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task AcknowledgeAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
