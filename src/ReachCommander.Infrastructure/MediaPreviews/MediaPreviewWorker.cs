using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class MediaPreviewWorker(
    MediaPreviewQueue queue,
    MediaPreviewService service,
    IMediaTranscodeRunner transcodeRunner,
    ILogger<MediaPreviewWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var sessionId in queue.ReadAllAsync(stoppingToken))
            {
                await service.ProcessQueuedAsync(sessionId, transcodeRunner, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "The media preview worker stopped unexpectedly.");
            throw;
        }
    }
}
