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
    ISystemUpdateOperationMonitor operationMonitor,
    TimeProvider clock,
    ILogger<SystemUpdateCoordinator> logger) : BackgroundService, ISystemUpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ManualCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    private readonly object _checkLock = new();
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private readonly CancellationTokenSource _monitorLifetime = new();
    private Task<SystemUpdateStatus>? _activeCheck;
    private Task? _activeMonitor;
    private string? _activeMonitorOperationId;
    private ISystemMutationDrain? _activeMonitorDrain;
    private int _disposed;
    private DateTimeOffset? _lastManualCheckStartedAt;
    private SystemUpdateStatus _status = SystemUpdateStatusFactory.Checking(clock.GetUtcNow());

    public async Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = ReadStatus();
        var eligible = await WithOperationEligibilityAsync(observed, cancellationToken)
            .ConfigureAwait(false);

        lock (_stateLock)
        {
            if (ReferenceEquals(_status, observed))
            {
                _status = eligible;
            }

            return _status;
        }
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

        ISystemMutationDrain? drain = null;
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

            drain = await mutationGate.BeginDrainAsync(DrainTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (drain is null)
            {
                throw new SystemUpdateFailedException();
            }

            if (await operationProbe.HasActiveOperationsAsync(cancellationToken)
                    .ConfigureAwait(false))
            {
                SetStatus(SystemUpdateStatusFactory.Blocked(
                    current.Channel!,
                    current.CurrentVersion!,
                    current.TargetVersion!,
                    current.LastCheckedAt,
                    clock.GetUtcNow()));
                throw new SystemUpdateBlockedByOperationsException();
            }

            var snapshot = await gateway.ApplyAsync(cancellationToken).ConfigureAwait(false);
            var applied = Map(snapshot, clock.GetUtcNow());
            SetStatus(applied);
            keepDrain = applied.Phase == SystemUpdatePhase.Applying;
            if (keepDrain)
            {
                StartOrJoinMonitor(snapshot, drain);
            }

            return applied;
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
            if (drain is not null && !keepDrain)
            {
                await drain.DisposeAsync().ConfigureAwait(false);
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _monitorLifetime.Cancel();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        Task? monitor;
        lock (_stateLock)
        {
            monitor = _activeMonitor;
        }

        if (monitor is not null)
        {
            await monitor.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _monitorLifetime.Cancel();
        _monitorLifetime.Dispose();
        _applyGate.Dispose();
        base.Dispose();
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
            var published = PublishDiscoveredStatus(status);
            if (published.Phase == SystemUpdatePhase.Applying)
            {
                StartOrJoinMonitor(snapshot, ownedDrain: null);
            }

            completion.TrySetResult(published);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completion.TrySetCanceled(cancellationToken);
        }
        catch (SystemUpdaterProtocolException)
        {
            var status = SystemUpdateStatusFactory.Incompatible(clock.GetUtcNow());
            completion.TrySetResult(PublishDiscoveryFailure(status));
        }
        catch (SystemUpdaterUnavailableException)
        {
            var status = SystemUpdateStatusFactory.Unavailable(clock.GetUtcNow());
            completion.TrySetResult(PublishDiscoveryFailure(status));
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "System update discovery failed with {ExceptionType}.",
                exception.GetType().Name);
            var status = SystemUpdateStatusFactory.Unavailable(clock.GetUtcNow());
            completion.TrySetResult(PublishDiscoveryFailure(status));
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

    private void StartOrJoinMonitor(
        UpdaterSnapshot snapshot,
        ISystemMutationDrain? ownedDrain)
    {
        var operationId = Required(snapshot.OperationId);
        TaskCompletionSource? owner = null;
        lock (_stateLock)
        {
            if (_activeMonitor is { IsCompleted: false })
            {
                if (string.Equals(
                        _activeMonitorOperationId,
                        operationId,
                        StringComparison.Ordinal))
                {
                    _activeMonitorDrain ??= ownedDrain;
                }

                return;
            }

            owner = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _activeMonitor = owner.Task;
            _activeMonitorOperationId = operationId;
            _activeMonitorDrain = ownedDrain;
        }

        _ = CompleteMonitorAsync(snapshot, owner);
    }

    private async Task CompleteMonitorAsync(
        UpdaterSnapshot applyingSnapshot,
        TaskCompletionSource completion)
    {
        ISystemMutationDrain? drain = null;
        try
        {
            var result = await operationMonitor.WaitForTerminalAsync(
                    applyingSnapshot,
                    PublishProgressSnapshot,
                    _monitorLifetime.Token)
                .ConfigureAwait(false);

            lock (_stateLock)
            {
                if (!ReferenceEquals(_activeMonitor, completion.Task))
                {
                    return;
                }

                if (_status.Phase == SystemUpdatePhase.Applying &&
                    string.Equals(
                        _status.OperationId,
                        _activeMonitorOperationId,
                        StringComparison.Ordinal))
                {
                    if (result.TerminalSnapshot is { } terminal)
                    {
                        var candidate = Map(terminal, clock.GetUtcNow());
                        if (IsTraceOlder(candidate.Trace, _status.Trace))
                        {
                            candidate = candidate with { Trace = _status.Trace };
                        }

                        _status = candidate;
                    }
                    else
                    {
                        _status = MonitorFailedStatus(_status);
                    }
                }

                drain = _activeMonitorDrain;
                ClearMonitorLocked();
            }
        }
        catch (OperationCanceledException) when (_monitorLifetime.IsCancellationRequested)
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_activeMonitor, completion.Task))
                {
                    ClearMonitorLocked();
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                "System update operation monitoring failed with {ExceptionType}.",
                exception.GetType().Name);
            lock (_stateLock)
            {
                if (ReferenceEquals(_activeMonitor, completion.Task))
                {
                    if (_status.Phase == SystemUpdatePhase.Applying &&
                        string.Equals(
                            _status.OperationId,
                            _activeMonitorOperationId,
                            StringComparison.Ordinal))
                    {
                        _status = MonitorFailedStatus(_status);
                    }

                    drain = _activeMonitorDrain;
                    ClearMonitorLocked();
                }
            }
        }
        finally
        {
            if (drain is not null)
            {
                await drain.DisposeAsync().ConfigureAwait(false);
            }

            completion.TrySetResult();
        }
    }

    private SystemUpdateStatus ReadStatus()
    {
        lock (_stateLock)
        {
            return _status;
        }
    }

    private void PublishProgressSnapshot(UpdaterSnapshot snapshot)
    {
        lock (_stateLock)
        {
            if (_activeMonitor is not { IsCompleted: false } ||
                snapshot.Phase != "applying" ||
                _status.Phase != SystemUpdatePhase.Applying ||
                !string.Equals(
                    snapshot.OperationId,
                    _activeMonitorOperationId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    _status.OperationId,
                    _activeMonitorOperationId,
                    StringComparison.Ordinal))
            {
                return;
            }

            SystemUpdateStatus candidate;
            try
            {
                candidate = Map(snapshot, clock.GetUtcNow());
            }
            catch (SystemUpdaterProtocolException)
            {
                return;
            }

            if (CanAdvance(_status.ProgressStage, candidate.ProgressStage) &&
                IsTraceOlder(candidate.Trace, _status.Trace))
            {
                candidate = candidate with { Trace = _status.Trace };
            }

            if (CanPublishProgress(_status, candidate))
            {
                _status = candidate;
            }
        }
    }

    private SystemUpdateStatus PublishDiscoveredStatus(SystemUpdateStatus candidate)
    {
        lock (_stateLock)
        {
            var sameOperation = string.Equals(
                _status.OperationId,
                candidate.OperationId,
                StringComparison.Ordinal);
            var currentIsTerminal = _status.Phase is
                SystemUpdatePhase.Completed or
                SystemUpdatePhase.RolledBack or
                SystemUpdatePhase.Failed;
            if (sameOperation &&
                currentIsTerminal &&
                candidate.Phase == SystemUpdatePhase.Applying)
            {
                return _status;
            }

            if (sameOperation && IsTraceOlder(candidate.Trace, _status.Trace))
            {
                candidate = candidate with { Trace = _status.Trace };
            }

            _status = candidate;
            return candidate;
        }
    }

    private void SetStatus(SystemUpdateStatus status)
    {
        lock (_stateLock)
        {
            _status = status;
        }
    }

    private SystemUpdateStatus PublishDiscoveryFailure(SystemUpdateStatus failure)
    {
        lock (_stateLock)
        {
            if (_status.Phase is
                SystemUpdatePhase.Applying or
                SystemUpdatePhase.Completed or
                SystemUpdatePhase.RolledBack or
                SystemUpdatePhase.Failed)
            {
                return _status;
            }

            _status = failure;
            return failure;
        }
    }

    private SystemUpdateStatus MonitorFailedStatus(SystemUpdateStatus current) =>
        SystemUpdateStatusFactory.Failed(
            current.Channel,
            current.CurrentVersion,
            current.TargetVersion,
            current.OperationId,
            current.LastCheckedAt,
            clock.GetUtcNow(),
            "The update status could not be recovered. Run reachcommander doctor on the host.",
            current.ProgressStage,
            current.Trace);

    private void ClearMonitorLocked()
    {
        _activeMonitor = null;
        _activeMonitorOperationId = null;
        _activeMonitorDrain = null;
    }

    private static SystemUpdateStatus Map(UpdaterSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.ProtocolVersion is not (1 or 2 or 3))
        {
            return SystemUpdateStatusFactory.Incompatible(now);
        }

        if (!snapshot.Supported)
        {
            return SystemUpdateStatusFactory.Unavailable(now);
        }

        var mapped = snapshot.Phase switch
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
                snapshot.UpdatedAt,
                Progress(snapshot.ProgressStage),
                snapshot.Trace),
            "completed" => SystemUpdateStatusFactory.Completed(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                Required(snapshot.TargetVersion),
                Required(snapshot.OperationId),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt,
                Progress(snapshot.ProgressStage),
                snapshot.Trace),
            "rolledBack" => SystemUpdateStatusFactory.RolledBack(
                Required(snapshot.Channel),
                Required(snapshot.CurrentVersion),
                Required(snapshot.TargetVersion),
                Required(snapshot.OperationId),
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt,
                Progress(snapshot.ProgressStage),
                snapshot.Trace),
            "failed" => SystemUpdateStatusFactory.Failed(
                snapshot.Channel,
                snapshot.CurrentVersion,
                snapshot.TargetVersion,
                snapshot.OperationId,
                snapshot.LastCheckedAt,
                snapshot.UpdatedAt,
                progressStage: Progress(snapshot.ProgressStage),
                trace: snapshot.Trace),
            _ => SystemUpdateStatusFactory.Incompatible(now),
        };
        return mapped with { ProtocolVersion = snapshot.ProtocolVersion };
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

    private static SystemUpdateProgressStage? Progress(string? stage) => stage switch
    {
        null => null,
        "downloading" => SystemUpdateProgressStage.Downloading,
        "installing" => SystemUpdateProgressStage.Installing,
        "restarting" => SystemUpdateProgressStage.Restarting,
        "healthChecking" => SystemUpdateProgressStage.HealthChecking,
        "restoring" => SystemUpdateProgressStage.Restoring,
        "restartingPrevious" => SystemUpdateProgressStage.RestartingPrevious,
        "verifyingRecovery" => SystemUpdateProgressStage.VerifyingRecovery,
        _ => throw new SystemUpdaterProtocolException(
            "The updater progress stage is incompatible."),
    };

    private static bool CanAdvance(
        SystemUpdateProgressStage? current,
        SystemUpdateProgressStage? candidate) => (current, candidate) switch
        {
            (null, SystemUpdateProgressStage.Downloading) => true,
            (SystemUpdateProgressStage.Downloading, SystemUpdateProgressStage.Installing) => true,
            (SystemUpdateProgressStage.Installing, SystemUpdateProgressStage.Restarting) => true,
            (SystemUpdateProgressStage.Restarting, SystemUpdateProgressStage.HealthChecking) => true,
            (SystemUpdateProgressStage.Restarting, SystemUpdateProgressStage.Restoring) => true,
            (SystemUpdateProgressStage.HealthChecking, SystemUpdateProgressStage.Restoring) => true,
            (SystemUpdateProgressStage.Restoring, SystemUpdateProgressStage.RestartingPrevious) => true,
            (SystemUpdateProgressStage.RestartingPrevious, SystemUpdateProgressStage.VerifyingRecovery) => true,
            _ => false,
        };

    private static bool CanPublishProgress(
        SystemUpdateStatus current,
        SystemUpdateStatus candidate) =>
        CanAdvance(current.ProgressStage, candidate.ProgressStage) ||
        (current.ProgressStage == candidate.ProgressStage &&
         !IsTraceOlder(candidate.Trace, current.Trace) &&
         candidate.Trace is not null);

    private static bool IsTraceOlder(
        SystemUpdateTrace? candidate,
        SystemUpdateTrace? current)
    {
        if (current is null)
        {
            return false;
        }

        if (candidate is null || candidate.StartedAt != current.StartedAt)
        {
            return true;
        }

        var candidateSequence = candidate.Events.Count == 0 ? 0 : candidate.Events[^1].Sequence;
        var currentSequence = current.Events.Count == 0 ? 0 : current.Events[^1].Sequence;
        return candidateSequence < currentSequence ||
               (candidateSequence == currentSequence &&
                candidate.ElapsedSeconds < current.ElapsedSeconds);
    }

    private static string PublicReason(string reasonCode) => reasonCode switch
    {
        "invalid_state" => "invalid_state",
        "release_unavailable" => "release_unavailable",
        "release_invalid" => "release_invalid",
        "manifest_unavailable" => "manifest_unavailable",
        "manifest_invalid" => "manifest_invalid",
        "updater_journal_invalid" => "updater_journal_invalid",
        "update_interrupted" => "update_interrupted",
        "update_command_timeout" => "update_command_timeout",
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
        "update_command_timeout" => "The host update command timed out and was stopped.",
        _ => "System updates are unavailable on this installation.",
    };
}
