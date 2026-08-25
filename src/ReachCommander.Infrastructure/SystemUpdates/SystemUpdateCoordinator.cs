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
    ISystemMutationGate mutationGate,
    ISystemUpdateOperationProbe operationProbe,
    TimeProvider clock,
    ILogger<SystemUpdateCoordinator> logger) : BackgroundService, ISystemUpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private readonly object _checkLock = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private Task<SystemUpdateStatus>? _activeCheck;
    private DateTimeOffset? _lastManualCheckStartedAt;
    private volatile SystemUpdateStatus _status = SystemUpdateStatusFactory.Checking(clock.GetUtcNow());

    public async Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await WithOperationEligibilityAsync(_status, cancellationToken)
            .ConfigureAwait(false);
        _status = status;
        return status;
    }

    public Task<SystemUpdateStatus> CheckAsync(CancellationToken cancellationToken) =>
        StartCheckAsync(enforceManualRateLimit: true, cancellationToken);

    private Task<SystemUpdateStatus> StartCheckAsync(
        bool enforceManualRateLimit,
        CancellationToken cancellationToken)
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
                var now = clock.GetUtcNow();
                if (enforceManualRateLimit &&
                    _lastManualCheckStartedAt is { } lastCheck &&
                    now - lastCheck < ManualCheckInterval)
                {
                    throw new SystemUpdateCheckRateLimitedException();
                }

                if (enforceManualRateLimit)
                {
                    _lastManualCheckStartedAt = now;
                }

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

        var drainStarted = false;
        var keepDrain = false;
        try
        {
            var current = await GetAsync(cancellationToken).ConfigureAwait(false);
            if (current.Phase == SystemUpdatePhase.Applying)
            {
                throw new SystemUpdateInProgressException();
            }

            if (current.Phase == SystemUpdatePhase.Blocked)
            {
                throw new SystemUpdateBlockedByOperationsException();
            }

            if (!current.CanApply)
            {
                throw new SystemUpdateUnavailableException();
            }

            drainStarted = true;
            if (!await mutationGate.BeginDrainAsync(DrainTimeout, cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new SystemUpdateFailedException();
            }

            if (await operationProbe.HasActiveOperationsAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                _status = SystemUpdateStatusFactory.Blocked(
                    current.Channel!,
                    current.CurrentVersion!,
                    current.TargetVersion!,
                    current.LastCheckedAt,
                    clock.GetUtcNow());
                throw new SystemUpdateBlockedByOperationsException();
            }

            var snapshot = await gateway.ApplyAsync(cancellationToken).ConfigureAwait(false);
            _status = Map(snapshot, clock.GetUtcNow());
            keepDrain = _status.Phase == SystemUpdatePhase.Applying;
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
            if (drainStarted && !keepDrain)
            {
                mutationGate.CancelDrain();
            }

            _applyGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var status = await StartCheckAsync(
                    enforceManualRateLimit: false,
                    stoppingToken)
                .ConfigureAwait(false);
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
            var status = await WithOperationEligibilityAsync(
                    Map(snapshot, clock.GetUtcNow()),
                    cancellationToken)
                .ConfigureAwait(false);
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

    private async Task<SystemUpdateStatus> WithOperationEligibilityAsync(
        SystemUpdateStatus status,
        CancellationToken cancellationToken)
    {
        if (status.Phase is not (SystemUpdatePhase.Available or SystemUpdatePhase.Blocked))
        {
            return status;
        }

        bool active;
        try
        {
            active = await operationProbe.HasActiveOperationsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "System update operation eligibility failed with {ExceptionType}.",
                exception.GetType().Name);
            active = true;
        }
        if (active)
        {
            return SystemUpdateStatusFactory.Blocked(
                status.Channel!,
                status.CurrentVersion!,
                status.TargetVersion!,
                status.LastCheckedAt,
                clock.GetUtcNow());
        }

        return SystemUpdateStatusFactory.Available(
            status.Channel!,
            status.CurrentVersion!,
            status.TargetVersion!,
            status.LastCheckedAt,
            status.UpdatedAt);
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
