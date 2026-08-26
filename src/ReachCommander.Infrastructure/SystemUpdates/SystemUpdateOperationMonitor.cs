using Microsoft.Extensions.Logging;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdateMonitorDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemUpdateMonitorDelay(TimeProvider timeProvider)
    : ISystemUpdateMonitorDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}

internal sealed record SystemUpdateMonitorResult(UpdaterSnapshot? TerminalSnapshot)
{
    public bool TimedOut => TerminalSnapshot is null;
}

internal interface ISystemUpdateOperationMonitor
{
    Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
        UpdaterSnapshot applyingSnapshot,
        CancellationToken cancellationToken);
}

internal sealed class SystemUpdateOperationMonitor(
    ISystemUpdaterGateway gateway,
    ISystemUpdateMonitorDelay delay,
    TimeProvider clock,
    ILogger<SystemUpdateOperationMonitor> logger)
    : ISystemUpdateOperationMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(6);

    public async Task<SystemUpdateMonitorResult> WaitForTerminalAsync(
        UpdaterSnapshot applyingSnapshot,
        CancellationToken cancellationToken)
    {
        var operationId = applyingSnapshot.OperationId;
        if (applyingSnapshot.Phase != "applying" || string.IsNullOrWhiteSpace(operationId))
        {
            throw new ArgumentException(
                "An applying snapshot with an operation ID is required.",
                nameof(applyingSnapshot));
        }

        var deadline = applyingSnapshot.UpdatedAt.Add(MaximumDuration);
        while (clock.GetUtcNow() < deadline)
        {
            var remaining = deadline - clock.GetUtcNow();
            await delay.DelayAsync(
                    remaining < PollInterval ? remaining : PollInterval,
                    cancellationToken)
                .ConfigureAwait(false);

            UpdaterSnapshot snapshot;
            try
            {
                snapshot = await gateway.CheckAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is SystemUpdaterUnavailableException or SystemUpdaterProtocolException)
            {
                logger.LogDebug(
                    "System update operation monitoring will retry after {ExceptionType}.",
                    exception.GetType().Name);
                continue;
            }

            if (snapshot.ProtocolVersion != SystemUpdateStatusFactory.ProtocolVersion ||
                !snapshot.Supported ||
                !string.Equals(snapshot.OperationId, operationId, StringComparison.Ordinal))
            {
                continue;
            }

            if (snapshot.Phase is "completed" or "rolledBack" or "failed")
            {
                return new SystemUpdateMonitorResult(snapshot);
            }
        }

        return new SystemUpdateMonitorResult(null);
    }
}
