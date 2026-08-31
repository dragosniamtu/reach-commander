using ReachCommander.Application.SourceManagement;

namespace ReachCommander.Infrastructure.SourceManagement;

internal sealed class SourceManagementMonitorDelay(TimeProvider timeProvider)
    : ISourceManagementMonitorDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}
