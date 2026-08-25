using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal sealed class SystemMutationGate : ISystemMutationGate
{
    private readonly object _lock = new();
    private TaskCompletionSource _empty = CompletedSource();
    private bool _draining;
    private int _activeLeases;

    public IAsyncDisposable? TryEnter()
    {
        lock (_lock)
        {
            if (_draining)
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

    public async Task<bool> BeginDrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        Task empty;
        lock (_lock)
        {
            _draining = true;
            empty = _empty.Task;
        }

        try
        {
            await empty.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    public void CancelDrain()
    {
        lock (_lock)
        {
            _draining = false;
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
}
