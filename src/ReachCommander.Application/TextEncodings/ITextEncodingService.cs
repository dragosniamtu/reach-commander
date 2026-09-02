namespace ReachCommander.Application.TextEncodings;

public interface ITextEncodingService
{
    ValueTask<TextEncodingPreview> PreviewAsync(
        TextEncodingPreviewRequest request,
        CancellationToken cancellationToken);

    ValueTask<TextEncodingOperation> ExecuteAsync(
        Guid planId,
        CancellationToken cancellationToken);

    ValueTask<TextEncodingOperation> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    ValueTask<TextEncodingOperation> CancelAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
