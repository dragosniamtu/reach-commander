using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdateDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemUpdateDelay(TimeProvider timeProvider) : ISystemUpdateDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, timeProvider, cancellationToken);
}

internal sealed class SystemUpdateCoordinator(
    ISystemUpdaterGateway gateway,
    ISystemUpdateDelay delay,
    TimeProvider clock,
    ILogger<SystemUpdateCoordinator> logger) : BackgroundService, ISystemUpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly object _checkLock = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private Task<SystemUpdateStatus>? _activeCheck;
    private volatile SystemUpdateStatus _status = SystemUpdateStatusFactory.Checking(clock.GetUtcNow());

    public Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_status);
    }

    public Task<SystemUpdateStatus> CheckAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<SystemUpdateStatus>? owner = null;
        Task<SystemUpdateStatus> task;
        lock (_checkLock)
        {
            if (_activeCheck is { IsCompleted: false })
            {
                task = _activeCheck;
            }
            else
            {
                owner = new TaskCompletionSource<SystemUpdateStatus>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                task = owner.Task;
                _activeCheck = task;
            }
        }

        if (owner is not null)
        {
            _ = CompleteCheckAsync(owner, cancellationToken);
        }

        return task.WaitAsync(cancellationToken);
    }

    public async Task<SystemUpdateStatus> ApplyAsync(CancellationToken cancellationToken)
    {
        if (!await _applyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SystemUpdateInProgressException();
        }

        try
        {
            var current = _status;
            if (current.Phase == SystemUpdatePhase.Applying)
            {
                throw new SystemUpdateInProgressException();
            }

            if (!current.CanApply)
            {
                throw new SystemUpdateUnavailableException();
            }

            var snapshot = await gateway.ApplyAsync(cancellationToken).ConfigureAwait(false);
            _status = Map(snapshot, clock.GetUtcNow());
            return _status;
        }
        catch (SystemUpdaterProtocolException)
        {
            throw new SystemUpdateProtocolIncompatibleException();
        }
        catch (SystemUpdaterUnavailableException)
        {
            throw new SystemUpdateUnavailableException();
        }
        finally
        {
            _applyGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = await CheckAsync(stoppingToken).ConfigureAwait(false);
            var interval = status.Supported && status.Phase != SystemUpdatePhase.Unavailable
                ? CheckInterval
                : RetryInterval;
            await delay.DelayAsync(interval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteCheckAsync(
        TaskCompletionSource<SystemUpdateStatus> completion,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await gateway.CheckAsync(cancellationToken).ConfigureAwait(false);
            var status = Map(snapshot, clock.GetUtcNow());
            _status = status;
            completion.TrySetResult(status);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (SystemUpdaterProtocolException)
        {
            var status = SystemUpdateStatusFactory.Incompatible(clock.GetUtcNow());
            _status = status;
            completion.TrySetResult(status);
        }
        catch (SystemUpdaterUnavailableException)
        {
            var status = SystemUpdateStatusFactory.Unavailable(clock.GetUtcNow());
            _status = status;
            completion.TrySetResult(status);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "System update discovery failed with {ExceptionType}.",
                exception.GetType().Name);
            var status = SystemUpdateStatusFactory.Unavailable(clock.GetUtcNow());
            _status = status;
            completion.TrySetResult(status);
        }
        finally
        {
            lock (_checkLock)
            {
                if (ReferenceEquals(_activeCheck, completion.Task))
                {
                    _activeCheck = null;
                }
            }
        }
    }

    private static SystemUpdateStatus Map(UpdaterSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.ProtocolVersion != SystemUpdateStatusFactory.ProtocolVersion)
        {
            return SystemUpdateStatusFactory.Incompatible(now);
        }

        if (!snapshot.Supported)
        {
            return SystemUpdateStatusFactory.Unavailable(now);
        }

        return snapshot.Phase switch
        {
            "unavailable" when snapshot.ReasonCode == "version_pinned" =>
                SystemUpdateStatusFactory.Pinned(
                    Required(snapshot.Channel),
                    Required(snapshot.CurrentVersion),
                    snapshot.LastCheckedAt,
                    snapshot.UpdatedAt),
            "unavailable" => SystemUpdateStatusFactory.SupportedUnavailable(
                snapshot.Channel,
                snapshot.CurrentVersion,
                PublicReason(snapshot.ReasonCode),
                PublicDetail(snapshot.ReasonCode),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            "current" => SystemUpdateStatusFactory.Current(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            "available" => SystemUpdateStatusFactory.Available(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                Required(snapshot.TargetVersion),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            "applying" => SystemUpdateStatusFactory.Applying(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                Required(snapshot.TargetVersion),
                Required(snapshot.OperationId),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            "completed" => SystemUpdateStatusFactory.Completed(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                Required(snapshot.TargetVersion),
                Required(snapshot.OperationId),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            "rolledBack" => SystemUpdateStatusFactory.RolledBack(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                Required(snapshot.TargetVersion),
                Required(snapshot.OperationId),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            "failed" => SystemUpdateStatusFactory.Failed(
                snapshot.Channel,
                snapshot.CurrentVersion,
                snapshot.TargetVersion,
                snapshot.OperationId,
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt),
            _ => SystemUpdateStatusFactory.Incompatible(now),
        };
    }

    private static string Required(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new SystemUpdaterProtocolException("The updater response is incomplete.")
            : value;

    private static string PublicReason(string reasonCode) => reasonCode switch
    {
        "invalid_state" => "invalid_state",
        "release_unavailable" => "release_unavailable",
        "release_invalid" => "release_invalid",
        "manifest_unavailable" => "manifest_unavailable",
        "manifest_invalid" => "manifest_invalid",
        "updater_journal_invalid" => "updater_journal_invalid",
        "update_interrupted" => "update_interrupted",
        _ => "system_update_unavailable",
    };

    private static string PublicDetail(string reasonCode) => reasonCode switch
    {
        "invalid_state" => "The trusted installer state is unavailable or invalid.",
        "release_unavailable" => "The stable release could not be checked.",
        "release_invalid" => "The stable release metadata is invalid.",
        "manifest_unavailable" => "The trusted container manifest could not be checked.",
        "manifest_invalid" => "The trusted container manifest metadata is invalid.",
        "updater_journal_invalid" => "The host update journal is invalid.",
        "update_interrupted" => "The host update service restarted during an update.",
        _ => "System updates are unavailable on this installation.",
    };
}
