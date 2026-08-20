namespace ReachCommander.Application.Archives;

public interface IArchiveExtractionService
{
    ValueTask<ArchiveExtractionPreview> PreviewAsync(
        ArchiveExtractionPreviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<ArchiveExtractionOperation> ExecuteAsync(
        string planId,
        CancellationToken cancellationToken);

    ValueTask<ArchiveExtractionOperation> GetAsync(
        string operationId,
        CancellationToken cancellationToken);

    ValueTask<ArchiveExtractionOperation> CancelAsync(
        string operationId,
        CancellationToken cancellationToken);
}
