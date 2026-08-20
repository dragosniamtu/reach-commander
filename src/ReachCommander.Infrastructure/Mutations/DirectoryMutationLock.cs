using System.Text;

namespace ReachCommander.Infrastructure.Mutations;

public sealed record DirectoryMutationTarget(string SourceId, string LogicalDirectory);

internal sealed class DirectoryMutationLock
{
    private static readonly StringComparison LogicalPathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly object _sync = new();
    private readonly List<MutationKey> _active = [];
    private readonly LinkedList<Waiter> _waiters = [];

    public ValueTask<IAsyncDisposable> AcquireAsync(
        string sourceId,
        string logicalDirectory,
        CancellationToken cancellationToken)
    {
        ValidateKey(sourceId, logicalDirectory);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IAsyncDisposable>(cancellationToken);
        }

        var key = new MutationKey(sourceId, logicalDirectory);
        Waiter? waiter = null;
        lock (_sync)
        {
            if (CanEnterImmediately(key))
            {
                _active.Add(key);
                return ValueTask.FromResult<IAsyncDisposable>(new Lease(this, key));
            }

            waiter = new Waiter(this, key);
            waiter.Node = _waiters.AddLast(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.Register(
                static state =>
                {
                    var request = (CancellationRequest)state!;
                    request.Owner.Cancel(request.Waiter, request.Token);
                },
                new CancellationRequest(this, waiter, cancellationToken));
        }

        return new ValueTask<IAsyncDisposable>(AwaitLeaseAsync(waiter));
    }

    public async ValueTask<IAsyncDisposable> AcquireManyAsync(
        IEnumerable<DirectoryMutationTarget> targets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var ordered = targets
            .Select(NormalizeTarget)
            .Distinct()
            .OrderBy(target => target.SourceId, StringComparer.Ordinal)
            .ThenBy(target => target.LogicalDirectory, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentException("At least one mutation target is required.", nameof(targets));
        }

        var collapsed = CollapseCoveredDescendants(ordered);
        var leases = new List<IAsyncDisposable>(collapsed.Count);
        try
        {
            foreach (var target in collapsed)
            {
                leases.Add(await AcquireAsync(
                    target.SourceId,
                    target.LogicalDirectory,
                    cancellationToken));
            }

            return new CompositeLease(leases);
        }
        catch
        {
            await DisposeReverseAsync(leases);
            throw;
        }
    }

    private static IReadOnlyList<DirectoryMutationTarget> CollapseCoveredDescendants(
        IReadOnlyList<DirectoryMutationTarget> ordered)
    {
        var collapsed = new List<DirectoryMutationTarget>(ordered.Count);
        foreach (var target in ordered)
        {
            if (collapsed.Any(existing =>
                    existing.SourceId.Equals(target.SourceId, StringComparison.Ordinal) &&
                    IsSameOrAncestor(existing.LogicalDirectory, target.LogicalDirectory)))
            {
                continue;
            }

            collapsed.Add(target);
        }

        return collapsed;
    }

    private static async Task<IAsyncDisposable> AwaitLeaseAsync(Waiter waiter)
    {
        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            waiter.Registration.Dispose();
        }
    }

    private bool CanEnterImmediately(MutationKey key) =>
        !_active.Any(active => Conflicts(active, key)) &&
        !_waiters.Any(waiter => Conflicts(waiter.Key, key));

    private void Cancel(Waiter waiter, CancellationToken cancellationToken)
    {
        List<Waiter> promoted;
        lock (_sync)
        {
            if (waiter.Node?.List is null)
            {
                return;
            }

            _waiters.Remove(waiter.Node);
            waiter.Node = null;
            promoted = PromoteEligibleWaiters();
        }

        waiter.Completion.TrySetCanceled(cancellationToken);
        CompletePromoted(promoted);
    }

    private void Release(MutationKey key)
    {
        List<Waiter> promoted;
        lock (_sync)
        {
            var activeIndex = _active.FindIndex(active => active.Equals(key));
            if (activeIndex < 0)
            {
                return;
            }

            _active.RemoveAt(activeIndex);
            promoted = PromoteEligibleWaiters();
        }

        CompletePromoted(promoted);
    }

    private List<Waiter> PromoteEligibleWaiters()
    {
        var promoted = new List<Waiter>();
        var node = _waiters.First;
        while (node is not null)
        {
            var next = node.Next;
            var waiter = node.Value;
            if (!_active.Any(active => Conflicts(active, waiter.Key)) &&
                !HasEarlierConflict(node, waiter.Key))
            {
                _waiters.Remove(node);
                waiter.Node = null;
                _active.Add(waiter.Key);
                promoted.Add(waiter);
            }

            node = next;
        }

        return promoted;
    }

    private static void CompletePromoted(IEnumerable<Waiter> promoted)
    {
        foreach (var waiter in promoted)
        {
            waiter.Completion.TrySetResult(new Lease(waiter.Owner, waiter.Key));
        }
    }

    private bool HasEarlierConflict(LinkedListNode<Waiter> node, MutationKey key)
    {
        for (var earlier = _waiters.First; earlier is not null && earlier != node; earlier = earlier.Next)
        {
            if (Conflicts(earlier.Value.Key, key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Conflicts(MutationKey left, MutationKey right)
    {
        if (!string.Equals(left.SourceId, right.SourceId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsSameOrAncestor(left.LogicalDirectory, right.LogicalDirectory) ||
            IsSameOrAncestor(right.LogicalDirectory, left.LogicalDirectory);
    }

    private static bool IsSameOrAncestor(string possibleAncestor, string path)
    {
        if (string.Equals(possibleAncestor, path, LogicalPathComparison) || possibleAncestor == "/")
        {
            return true;
        }

        return path.StartsWith($"{possibleAncestor}/", LogicalPathComparison);
    }

    private static void ValidateKey(string sourceId, string logicalDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalDirectory);
        if (!logicalDirectory.StartsWith('/') ||
            (logicalDirectory.Length > 1 && logicalDirectory.EndsWith('/')) ||
            logicalDirectory.Contains("//", StringComparison.Ordinal) ||
            logicalDirectory.Contains('\\') ||
            logicalDirectory.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The logical directory must already be normalized.", nameof(logicalDirectory));
        }
    }

    private static DirectoryMutationTarget NormalizeTarget(DirectoryMutationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateKey(target.SourceId, target.LogicalDirectory);
        var sourceId = target.SourceId.Normalize(NormalizationForm.FormC).ToUpperInvariant();
        var logicalDirectory = target.LogicalDirectory.Normalize(NormalizationForm.FormC);
        if (OperatingSystem.IsWindows())
        {
            logicalDirectory = logicalDirectory.ToUpperInvariant();
        }

        return new DirectoryMutationTarget(sourceId, logicalDirectory);
    }

    private static async ValueTask DisposeReverseAsync(IReadOnlyList<IAsyncDisposable> leases)
    {
        for (var index = leases.Count - 1; index >= 0; index--)
        {
            await leases[index].DisposeAsync();
        }
    }

    private sealed record MutationKey(string SourceId, string LogicalDirectory);

    private sealed class Waiter(DirectoryMutationLock owner, MutationKey key)
    {
        public MutationKey Key { get; } = key;

        public DirectoryMutationLock Owner { get; } = owner;

        public TaskCompletionSource<IAsyncDisposable> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed record CancellationRequest(
        DirectoryMutationLock Owner,
        Waiter Waiter,
        CancellationToken Token);

    private sealed class Lease(DirectoryMutationLock owner, MutationKey key) : IAsyncDisposable
    {
        private DirectoryMutationLock? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.Release(key);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CompositeLease(IReadOnlyList<IAsyncDisposable> leases) : IAsyncDisposable
    {
        private IReadOnlyList<IAsyncDisposable>? _leases = leases;

        public async ValueTask DisposeAsync()
        {
            var owned = Interlocked.Exchange(ref _leases, null);
            if (owned is not null)
            {
                await DisposeReverseAsync(owned);
            }
        }
    }
}
