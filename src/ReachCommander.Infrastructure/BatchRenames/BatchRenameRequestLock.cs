namespace ReachCommander.Infrastructure.BatchRenames;

internal sealed class BatchRenameRequestLock
{
    private readonly object _gate = new();
    private readonly Dictionary<RequestKey, LockEntry> _entries = [];

    public ValueTask<IAsyncDisposable> AcquirePlanAsync(
        Guid planId,
        CancellationToken cancellationToken) =>
        AcquireAsync(new RequestKey(RequestKind.ExecutePlan, planId), cancellationToken);

    public ValueTask<IAsyncDisposable> AcquireUndoAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        AcquireAsync(new RequestKey(RequestKind.UndoOperation, operationId), cancellationToken);

    private async ValueTask<IAsyncDisposable> AcquireAsync(
        RequestKey key,
        CancellationToken cancellationToken)
    {
        LockEntry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new LockEntry();
                _entries.Add(key, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    private void Release(RequestKey key, LockEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(key, entry);
    }

    private void ReleaseReference(RequestKey key, LockEntry entry)
    {
        var dispose = false;
        lock (_gate)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 &&
                _entries.TryGetValue(key, out var current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                dispose = true;
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private enum RequestKind
    {
        ExecutePlan,
        UndoOperation,
    }

    private readonly record struct RequestKey(RequestKind Kind, Guid Id);

    private sealed class LockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class Lease(
        BatchRenameRequestLock owner,
        RequestKey key,
        LockEntry entry) : IAsyncDisposable
    {
        private BatchRenameRequestLock? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(key, entry);
            return ValueTask.CompletedTask;
        }
    }
}
