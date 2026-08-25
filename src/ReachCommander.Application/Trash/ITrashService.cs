using ReachCommander.Application.FileOperations;

namespace ReachCommander.Application.Trash;

public interface ITrashService
{
    Task<DeletePreview> PreviewDeleteAsync(
        DeletePreviewRequest request,
        CancellationToken cancellationToken);

    Task<FileOperationStatus> SubmitDeleteAsync(
        DeleteSubmission request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<TrashEntry>> ListAsync(
        string? sourceId,
        CancellationToken cancellationToken);

    Task<RestorePreview> PreviewRestoreAsync(
        RestorePreviewRequest request,
        CancellationToken cancellationToken);

    Task<FileOperationStatus> SubmitRestoreAsync(
        RestoreSubmission request,
        CancellationToken cancellationToken);

    Task<FileOperationStatus> PermanentlyDeleteAsync(
        TrashPermanentDeleteRequest request,
        CancellationToken cancellationToken);

    Task<FileOperationStatus> EmptyAsync(
        EmptyTrashRequest request,
        CancellationToken cancellationToken);
}
