using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal sealed class SystemMutationGate : ISystemMutationGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _empty = CompletedSource();
    private DrainLease? _activeDrain;
    private int _activeLeases;

    public IAsyncDisposable? TryEnter()
    {
        lock (_lock)
        {
            if (_activeDrain is not null)
            {
                return null;
            }

            if (_activeLeases++ == 0)
            {
                _empty = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            return new Lease(this);
        }
    }

    public async Task<ISystemMutationDrain?> BeginDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task empty;
        DrainLease drain;
        lock (_lock)
        {
            if (_activeDrain is not null)
            {
                return null;
            }

            drain = new DrainLease(this);
            _activeDrain = drain;
            empty = _empty.Task;
        }

        try
        {
            await empty.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return drain;
        }
        catch (TimeoutException)
        {
            await drain.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await drain.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void ReleaseDrain(DrainLease drain)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_activeDrain, drain))
            {
                _activeDrain = null;
            }
        }
    }

    private void Exit()
    {
        lock (_lock)
        {
            if (_activeLeases <= 0)
            {
                return;
            }

            if (--_activeLeases == 0)
            {
                _empty.TrySetResult();
            }
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private sealed class Lease(SystemMutationGate owner) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Exit();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class DrainLease(SystemMutationGate owner) : ISystemMutationDrain
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseDrain(this);
            }

            return ValueTask.CompletedTask;
        }
    }
}
