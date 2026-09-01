using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class MediaPreviewCleanupService(
    MediaPreviewService service,
    TimeProvider clock,
    IOptions<MediaPreviewOptions> options) : BackgroundService
{
    private readonly TimeSpan _interval = options.Value.CleanupInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        service.DeleteRecoveredOutputs();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_interval, clock, stoppingToken);
            service.DeleteAbandonedPendingOutputs();
            service.DeleteExpiredOutputs();
        }
    }
}
