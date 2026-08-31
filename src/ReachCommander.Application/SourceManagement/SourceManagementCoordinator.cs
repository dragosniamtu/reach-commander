using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Application.SourceManagement;

public sealed class SourceManagementCoordinator(
    ISourceManagementGateway gateway,
    ISourceManagementOperationEligibility operationEligibility,
    ISystemMutationGate mutationGate,
    ISystemUpdateService systemUpdates,
    ISourceManagementMonitorDelay monitorDelay) : ISourceManagementService, IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MonitorInterval = TimeSpan.FromSeconds(2);
    private const int MaximumMonitorAttempts = 600;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Guid? _activeOperationId;
    private ISystemMutationDrain? _activeDrain;
    private Task? _activeMonitor;
    private Guid? _activeMonitorOperationId;
    private int _disposed;

    public Task<SourceManagementCapability> GetStatusAsync(
        CancellationToken cancellationToken) => gateway.GetStatusAsync(cancellationToken);

    public async Task<SourceManagementOperation> AddAsync(
        SourceAddRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        Validate(request);
        if (!await _mutation.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new SourceManagementBusyException();
        }

        ISystemMutationDrain? drain = null;
        var retainDrain = false;
        try
        {
            lock (_stateLock)
            {
                if (_activeOperationId is not null || _activeDrain is not null)
                {
                    throw new SourceManagementBusyException();
                }
            }

            var capability = await gateway.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            EnsureSupported(capability);
            await EnsureRestartSafeAsync(cancellationToken).ConfigureAwait(false);

            drain = await mutationGate.BeginDrainAsync(DrainTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (drain is null)
            {
                throw new SourceManagementBlockedException();
            }

            await EnsureRestartSafeAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SourceManagementOperation operation;
            try
            {
                operation = await gateway.AddAsync(request, _lifetime.Token).ConfigureAwait(false);
            }
            catch (SourceManagementMutationOutcomeUnknownException)
            {
                retainDrain = true;
                TrackAmbiguousOutcome(drain);
                throw;
            }

            retainDrain = !operation.IsTerminal;
            if (retainDrain)
            {
                TrackOperation(operation.OperationId, drain);
            }

            return operation;
        }
        finally
        {
            if (drain is not null && !retainDrain)
            {
                await drain.DisposeAsync().ConfigureAwait(false);
            }

            _mutation.Release();
        }
    }

    public async Task<SourceManagementOperation> GetOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (operationId == Guid.Empty)
        {
            throw new SourceManagementValidationException();
        }

        var operation = await gateway.GetOperationAsync(operationId, cancellationToken)
            .ConfigureAwait(false);
        if (operation.IsTerminal)
        {
            await ReleaseTrackedOperationAsync(operationId).ConfigureAwait(false);
        }
        else
        {
            await JoinObservedOperationAsync(operationId, cancellationToken)
                .ConfigureAwait(false);
        }

        return operation;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        Task? monitor;
        lock (_stateLock)
        {
            monitor = _activeMonitor;
        }

        if (monitor is not null)
        {
            try
            {
                await monitor.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await ReleaseTrackedOperationAsync(expectedOperationId: null).ConfigureAwait(false);
        _lifetime.Dispose();
        _mutation.Dispose();
    }

    private async Task JoinObservedOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        ISystemMutationDrain? drain = null;
        var retainDrain = false;
        try
        {
            lock (_stateLock)
            {
                if (_activeOperationId == operationId)
                {
                    retainDrain = true;
                }
                else if (_activeOperationId is not null || _activeDrain is not null)
                {
                    throw new SourceManagementBusyException();
                }
            }

            if (!retainDrain)
            {
                drain = await mutationGate.BeginDrainAsync(DrainTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (drain is null)
                {
                    throw new SourceManagementBlockedException();
                }

                retainDrain = true;
                TrackOperation(operationId, drain);
            }
            else
            {
                TrackOperation(operationId, ownedDrain: null);
            }
        }
        finally
        {
            if (drain is not null && !retainDrain)
            {
                await drain.DisposeAsync().ConfigureAwait(false);
            }

            _mutation.Release();
        }
    }

    private void TrackOperation(Guid operationId, ISystemMutationDrain? ownedDrain)
    {
        TaskCompletionSource? monitorOwner = null;
        lock (_stateLock)
        {
            if (_activeOperationId is { } active && active != operationId)
            {
                throw new SourceManagementBusyException();
            }

            _activeOperationId = operationId;
            _activeDrain ??= ownedDrain;
            if (_activeMonitor is not { IsCompleted: false } ||
                _activeMonitorOperationId != operationId)
            {
                monitorOwner = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeMonitor = monitorOwner.Task;
                _activeMonitorOperationId = operationId;
            }
        }

        if (monitorOwner is not null)
        {
            _ = CompleteMonitorAsync(operationId, monitorOwner);
        }
    }

    private void TrackAmbiguousOutcome(ISystemMutationDrain ownedDrain)
    {
        TaskCompletionSource? monitorOwner = null;
        lock (_stateLock)
        {
            if (_activeOperationId is not null || _activeDrain is not null)
            {
                throw new SourceManagementBusyException();
            }

            _activeDrain = ownedDrain;
            monitorOwner = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeMonitor = monitorOwner.Task;
            _activeMonitorOperationId = null;
        }

        _ = CompleteAmbiguousMonitorAsync(ownedDrain, monitorOwner);
    }

    private async Task CompleteAmbiguousMonitorAsync(
        ISystemMutationDrain ownedDrain,
        TaskCompletionSource completion)
    {
        try
        {
            await monitorDelay.DelayAsync(
                    MonitorInterval * MaximumMonitorAttempts,
                    _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            var releaseDrain = false;
            lock (_stateLock)
            {
                if (ReferenceEquals(_activeMonitor, completion.Task) &&
                    ReferenceEquals(_activeDrain, ownedDrain))
                {
                    _activeMonitor = null;
                    _activeMonitorOperationId = null;
                    _activeDrain = null;
                    releaseDrain = true;
                }
            }

            completion.TrySetResult();
            if (releaseDrain)
            {
                await ownedDrain.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task CompleteMonitorAsync(
        Guid operationId,
        TaskCompletionSource completion)
    {
        try
        {
            for (var attempt = 0; attempt < MaximumMonitorAttempts; attempt++)
            {
                await monitorDelay.DelayAsync(MonitorInterval, _lifetime.Token)
                    .ConfigureAwait(false);
                try
                {
                    var operation = await gateway
                        .GetOperationAsync(operationId, _lifetime.Token)
                        .ConfigureAwait(false);
                    if (operation.OperationId != operationId)
                    {
                        break;
                    }

                    if (operation.IsTerminal)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    break;
                }
                catch (SourceManagementException)
                {
                    // A container restart can temporarily remove the local socket.
                }
                catch
                {
                    // Fail closed until the bounded monitor window expires.
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            ISystemMutationDrain? drain = null;
            lock (_stateLock)
            {
                if (ReferenceEquals(_activeMonitor, completion.Task))
                {
                    _activeMonitor = null;
                    _activeMonitorOperationId = null;
                    if (_activeOperationId == operationId)
                    {
                        _activeOperationId = null;
                        drain = _activeDrain;
                        _activeDrain = null;
                    }
                }
            }

            completion.TrySetResult();
            if (drain is not null)
            {
                await drain.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReleaseTrackedOperationAsync(Guid? expectedOperationId)
    {
        ISystemMutationDrain? drain = null;
        lock (_stateLock)
        {
            if (_activeOperationId is null ||
                (expectedOperationId is not null &&
                 _activeOperationId != expectedOperationId))
            {
                return;
            }

            _activeOperationId = null;
            drain = _activeDrain;
            _activeDrain = null;
        }

        if (drain is not null)
        {
            await drain.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task EnsureRestartSafeAsync(CancellationToken cancellationToken)
    {
        var update = await systemUpdates.GetAsync(cancellationToken).ConfigureAwait(false);
        if (update.Phase == SystemUpdatePhase.Applying)
        {
            throw new SourceManagementBusyException();
        }

        bool hasActiveOperations;
        try
        {
            hasActiveOperations = await operationEligibility
                .HasActiveOperationsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            hasActiveOperations = true;
        }

        if (hasActiveOperations)
        {
            throw new SourceManagementBlockedException();
        }
    }

    private static void EnsureSupported(SourceManagementCapability capability)
    {
        if (capability.Supported)
        {
            return;
        }

        if (capability.ReasonCode == "installer_upgrade_required")
        {
            throw new SourceManagementProtocolIncompatibleException();
        }

        throw new SourceManagementUnavailableException();
    }

    private static void Validate(SourceAddRequest request)
    {
        if (request is null ||
            string.IsNullOrWhiteSpace(request.DisplayName) ||
            request.DisplayName.Trim().Length > 80 ||
            request.DisplayName.Any(char.IsControl) ||
            string.IsNullOrEmpty(request.HostPath) ||
            request.HostPath.Length > 1024 ||
            !request.HostPath.StartsWith("/", StringComparison.Ordinal) ||
            request.HostPath.Contains('\\') ||
            request.HostPath.Any(char.IsControl) ||
            !Enum.IsDefined(request.Access))
        {
            throw new SourceManagementValidationException();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
