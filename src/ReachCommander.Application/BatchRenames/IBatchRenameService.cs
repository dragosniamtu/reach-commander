namespace ReachCommander.Application.BatchRenames;

public interface IBatchRenameService
{
    ValueTask<BatchRenamePreview> PreviewAsync(
        BatchRenamePreviewCommand command,
        CancellationToken cancellationToken);

    ValueTask<BatchRenamePreview> PreviewExactAsync(
        ExactRenamePreviewCommand command,
        CancellationToken cancellationToken);

    ValueTask<BatchRenameOperationResult> ExecuteAsync(
        Guid planId,
        CancellationToken cancellationToken);

    ValueTask<BatchRenameOperationResult> UndoAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
