namespace ReachCommander.Application.MediaPreviews;

public interface IMediaPreviewService
{
    ValueTask<MediaPreviewSession> CreateAsync(
        CreateMediaPreviewCommand command,
        CancellationToken cancellationToken);

    ValueTask<MediaPreviewSession> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    ValueTask<MediaPreviewSession> RequestFallbackAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    ValueTask<MediaPreviewSession> SelectSubtitleAsync(
        Guid sessionId,
        string subtitlePath,
        CancellationToken cancellationToken);

    ValueTask<MediaAsset> OpenDirectContentAsync(
        Guid sessionId,
        CancellationToken cancellationToken);

    ValueTask<MediaAsset> OpenHlsAssetAsync(
        Guid sessionId,
        string assetName,
        CancellationToken cancellationToken);

    ValueTask CloseAsync(
        Guid sessionId,
        CancellationToken cancellationToken);
}
