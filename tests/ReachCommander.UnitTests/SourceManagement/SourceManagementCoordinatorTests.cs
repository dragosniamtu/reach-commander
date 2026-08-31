using ReachCommander.Application.SourceManagement;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SourceManagement;

public sealed class SourceManagementCoordinatorTests
{
    private static readonly SourceAddRequest Request =
        new("Archive", "/srv/archive", SourceAccess.ReadOnly);

    [Fact]
    public async Task Unsupported_capability_returns_without_draining()
    {
        var gateway = new StubGateway
        {
            Capability = new(false, "unsupported_platform", "Source management is unavailable on this platform."),
        };
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(gateway, gate: gate);

        var capability = await coordinator.GetStatusAsync(default);

        Assert.False(capability.Supported);
        Assert.Equal(0, gate.BeginDrainCount);
    }

    [Fact]
    public async Task Active_file_operation_blocks_before_host_mutation()
    {
        var gateway = new StubGateway();
        var eligibility = new StubEligibility { Active = true };
        var coordinator = Coordinator(gateway, eligibility: eligibility);

        var exception = await Assert.ThrowsAsync<SourceManagementBlockedException>(() =>
            coordinator.AddAsync(Request, default));

        Assert.Equal("source_management_blocked_by_operations", exception.Code);
        Assert.Equal(0, gateway.AddCount);
    }

    [Fact]
    public async Task Source_post_owns_drain_and_rechecks_operations_after_drain()
    {
        var gateway = new StubGateway();
        var eligibility = new StubEligibility { Sequence = new Queue<bool>([false, true]) };
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(gateway, eligibility, gate);

        await Assert.ThrowsAsync<SourceManagementBlockedException>(() =>
            coordinator.AddAsync(Request, default));

        Assert.Equal(1, gate.BeginDrainCount);
        Assert.Equal(1, gate.CancelDrainCount);
        Assert.Equal(0, gateway.AddCount);
    }

    [Fact]
    public async Task Concurrent_source_mutation_is_rejected()
    {
        var gateway = new StubGateway { BlockAdd = true };
        var coordinator = Coordinator(gateway);
        var first = coordinator.AddAsync(Request, default);
        await gateway.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<SourceManagementBusyException>(() =>
            coordinator.AddAsync(Request, default));

        gateway.ReleaseAdd.TrySetResult();
        await first;
    }

    [Fact]
    public async Task Applying_system_update_blocks_before_and_after_drain()
    {
        var gateway = new StubGateway();
        var updates = new StubUpdateService
        {
            Statuses = new Queue<SystemUpdateStatus>(
            [CurrentUpdateStatus(), ApplyingUpdateStatus()]),
        };
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(gateway, gate: gate, updates: updates);

        await Assert.ThrowsAsync<SourceManagementBusyException>(() =>
            coordinator.AddAsync(Request, default));

        Assert.Equal(1, gate.BeginDrainCount);
        Assert.Equal(1, gate.CancelDrainCount);
        Assert.Equal(0, gateway.AddCount);
    }

    [Fact]
    public async Task Caller_cancellation_after_submission_is_shielded_until_acceptance()
    {
        var gateway = new StubGateway { BlockAdd = true };
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(gateway, gate: gate);
        using var cancellation = new CancellationTokenSource();
        var pending = coordinator.AddAsync(Request, cancellation.Token);
        await gateway.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        Assert.False(pending.IsCompleted);
        gateway.ReleaseAdd.TrySetResult();
        var accepted = await pending;

        Assert.Equal(SourceManagementPhase.Accepted, accepted.Phase);
        Assert.Equal(0, gate.CancelDrainCount);
    }

    [Fact]
    public async Task Ambiguous_submission_retains_drain_for_the_bounded_safety_window()
    {
        var gateway = new StubGateway { UnknownAddOutcome = true };
        var gate = new TrackingMutationGate();
        var delay = new ManualMonitorDelay();
        var coordinator = Coordinator(gateway, gate: gate, monitorDelay: delay);

        await Assert.ThrowsAsync<SourceManagementMutationOutcomeUnknownException>(() =>
            coordinator.AddAsync(Request, default));
        await delay.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, gate.CancelDrainCount);
        await Assert.ThrowsAsync<SourceManagementBusyException>(() =>
            coordinator.AddAsync(Request, default));

        delay.Release.TrySetResult();
        await gate.DrainCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, gate.CancelDrainCount);
    }

    [Fact]
    public async Task Accepted_operation_keeps_mutations_blocked_until_terminal_status()
    {
        var gateway = new StubGateway();
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(gateway, gate: gate);

        var accepted = await coordinator.AddAsync(Request, default);
        Assert.Equal(0, gate.CancelDrainCount);

        gateway.Operation = gateway.Operation with
        {
            Phase = SourceManagementPhase.Completed,
            ReasonCode = "completed",
            Detail = "The source has been added.",
            SourceId = "archive",
            DisplayName = "Archive",
        };
        await coordinator.GetOperationAsync(accepted.OperationId, default);

        Assert.Equal(1, gate.CancelDrainCount);
    }

    [Fact]
    public async Task Accepted_operation_releases_drain_after_host_finishes_without_browser_polling()
    {
        var completed = CompletedOperation();
        var gateway = new StubGateway { ObservedOperation = completed };
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(
            gateway,
            gate: gate,
            monitorDelay: new ImmediateMonitorDelay());

        await coordinator.AddAsync(Request, default);
        await gate.DrainCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(1, gate.CancelDrainCount);
        Assert.True(gateway.GetOperationCount >= 1);
    }

    [Fact]
    public async Task Reconnected_nonterminal_operation_is_joined_and_releases_on_completion()
    {
        var accepted = new StubGateway().Operation;
        var gateway = new StubGateway
        {
            ObservedOperations = new Queue<SourceManagementOperation>(
                [accepted, CompletedOperation()]),
        };
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(
            gateway,
            gate: gate,
            monitorDelay: new ImmediateMonitorDelay());

        var observed = await coordinator.GetOperationAsync(accepted.OperationId, default);
        await gate.DrainCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(SourceManagementPhase.Accepted, observed.Phase);
        Assert.Equal(1, gate.BeginDrainCount);
        Assert.Equal(1, gate.CancelDrainCount);
    }

    [Fact]
    public async Task Disposing_coordinator_releases_an_owned_drain()
    {
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(new StubGateway(), gate: gate);
        await coordinator.AddAsync(Request, default);

        await coordinator.DisposeAsync();

        Assert.Equal(1, gate.CancelDrainCount);
    }

    [Fact]
    public async Task Completing_monitor_cannot_orphan_a_newly_accepted_operation()
    {
        var firstId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var secondId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var gateway = new StubGateway
        {
            AddOperations = new Queue<SourceManagementOperation>(
            [AcceptedOperation(firstId), AcceptedOperation(secondId)]),
            ObservedOperations = new Queue<SourceManagementOperation>(
            [CompletedOperation(firstId), CompletedOperation(secondId)]),
        };
        var delay = new ReleaseFirstMonitorDelay();
        var gate = new BlockingFirstCancelMutationGate();
        var coordinator = Coordinator(gateway, gate: gate, monitorDelay: delay);

        await coordinator.AddAsync(Request, default);
        delay.ReleaseFirst.TrySetResult();
        await gate.FirstCancelStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await coordinator.AddAsync(Request, default);
        await gate.SecondCancelCompleted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        gate.AllowFirstCancelReturn.Set();

        Assert.Equal(2, gate.CancelDrainCount);
    }

    [Fact]
    public async Task Terminal_browser_poll_cannot_orphan_the_next_accepted_operation()
    {
        var firstId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var secondId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var gateway = new StubGateway
        {
            AddOperations = new Queue<SourceManagementOperation>(
                [AcceptedOperation(firstId), AcceptedOperation(secondId)]),
            ObservedOperations = new Queue<SourceManagementOperation>(
                [CompletedOperation(firstId), CompletedOperation(secondId)]),
        };
        var delay = new ReleaseFirstMonitorDelay();
        var gate = new TrackingMutationGate();
        var coordinator = Coordinator(gateway, gate: gate, monitorDelay: delay);

        await coordinator.AddAsync(Request, default);
        await coordinator.GetOperationAsync(firstId, default);
        await coordinator.AddAsync(Request, default);
        await gate.SecondDrainCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        delay.ReleaseFirst.TrySetResult();

        Assert.Equal(2, gate.CancelDrainCount);
    }

    private static SourceManagementCoordinator Coordinator(
        StubGateway gateway,
        StubEligibility? eligibility = null,
        ISystemMutationGate? gate = null,
        StubUpdateService? updates = null,
        ISourceManagementMonitorDelay? monitorDelay = null) => new(
            gateway,
            eligibility ?? new StubEligibility(),
            gate ?? new TrackingMutationGate(),
            updates ?? new StubUpdateService(),
            monitorDelay ?? new BlockingMonitorDelay());

    private static SourceManagementOperation CompletedOperation() =>
        CompletedOperation(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    private static SourceManagementOperation CompletedOperation(Guid operationId) => new(
        operationId,
        "archive",
        "Archive",
        SourceManagementPhase.Completed,
        "completed",
        "The source has been added.",
        DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
        DateTimeOffset.Parse("2026-08-31T10:00:01Z"));

    private static SourceManagementOperation AcceptedOperation(Guid operationId) => new(
        operationId,
        null,
        null,
        SourceManagementPhase.Accepted,
        "accepted",
        "Source change accepted.",
        DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
        DateTimeOffset.Parse("2026-08-31T10:00:00Z"));

    private static SystemUpdateStatus CurrentUpdateStatus() =>
        SystemUpdateStatusFactory.Current(
            "stable",
            "v1.0.0",
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"));

    private static SystemUpdateStatus ApplyingUpdateStatus() =>
        SystemUpdateStatusFactory.Applying(
            "stable",
            "v1.0.0",
            "v1.1.0",
            "update-1",
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T10:00:01Z"));

    private sealed class StubGateway : ISourceManagementGateway
    {
        public SourceManagementCapability Capability { get; set; } =
            new(true, "supported", "Source management is available.");

        public SourceManagementOperation Operation { get; set; } = new(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            null,
            null,
            SourceManagementPhase.Accepted,
            "accepted",
            "Source change accepted.",
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-31T10:00:00Z"));

        public int AddCount { get; private set; }

        public int GetOperationCount { get; private set; }

        public SourceManagementOperation? ObservedOperation { get; set; }

        public Queue<SourceManagementOperation>? ObservedOperations { get; set; }

        public Queue<SourceManagementOperation>? AddOperations { get; set; }

        public bool BlockAdd { get; set; }

        public bool CancelAdd { get; set; }

        public bool UnknownAddOutcome { get; set; }

        public TaskCompletionSource AddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseAdd { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SourceManagementCapability> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Capability);

        public async Task<SourceManagementOperation> AddAsync(
            SourceAddRequest request,
            CancellationToken cancellationToken)
        {
            AddCount++;
            AddStarted.TrySetResult();
            if (UnknownAddOutcome)
            {
                throw new SourceManagementMutationOutcomeUnknownException();
            }

            if (CancelAdd)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (BlockAdd)
            {
                await ReleaseAdd.Task.WaitAsync(cancellationToken);
            }

            return AddOperations is { Count: > 0 } ? AddOperations.Dequeue() : Operation;
        }

        public Task<SourceManagementOperation> GetOperationAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            GetOperationCount++;
            return Task.FromResult(ObservedOperations is { Count: > 0 }
                ? ObservedOperations.Dequeue()
                : ObservedOperation ?? Operation);
        }
    }

    private sealed class StubEligibility : ISourceManagementOperationEligibility
    {
        public bool Active { get; set; }

        public Queue<bool>? Sequence { get; set; }

        public Task<bool> HasActiveOperationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Sequence is { Count: > 0 } ? Sequence.Dequeue() : Active);
    }

    private sealed class TrackingMutationGate : ISystemMutationGate
    {
        public int BeginDrainCount { get; private set; }

        public int CancelDrainCount { get; private set; }

        public TaskCompletionSource DrainCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondDrainCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncDisposable? TryEnter() => throw new NotSupportedException();

        public Task<ISystemMutationDrain?> BeginDrainAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            BeginDrainCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ISystemMutationDrain?>(new TrackingDrain(this));
        }

        private void CancelDrain()
        {
            CancelDrainCount++;
            DrainCancelled.TrySetResult();
            if (CancelDrainCount >= 2)
            {
                SecondDrainCancelled.TrySetResult();
            }
        }

        private sealed class TrackingDrain(TrackingMutationGate owner) : ISystemMutationDrain
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.CancelDrain();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class BlockingMonitorDelay : ISourceManagementMonitorDelay
    {
        private readonly TaskCompletionSource _delay =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            _delay.Task.WaitAsync(cancellationToken);
    }

    private sealed class ImmediateMonitorDelay : ISourceManagementMonitorDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ManualMonitorDelay : ISourceManagementMonitorDelay
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ReleaseFirstMonitorDelay : ISourceManagementMonitorDelay
    {
        private int _count;

        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _count) == 1
                ? ReleaseFirst.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask;
    }

    private sealed class BlockingFirstCancelMutationGate : ISystemMutationGate
    {
        private int _cancelCount;

        public int CancelDrainCount => Volatile.Read(ref _cancelCount);

        public TaskCompletionSource FirstCancelStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondCancelCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim AllowFirstCancelReturn { get; } = new(false);

        public IAsyncDisposable? TryEnter() => throw new NotSupportedException();

        public Task<ISystemMutationDrain?> BeginDrainAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken) => Task.FromResult<ISystemMutationDrain?>(
                new BlockingDrain(this));

        private void CancelDrain()
        {
            var count = Interlocked.Increment(ref _cancelCount);
            if (count == 1)
            {
                FirstCancelStarted.TrySetResult();
                AllowFirstCancelReturn.Wait(TimeSpan.FromSeconds(2));
            }
            else
            {
                SecondCancelCompleted.TrySetResult();
            }
        }

        private sealed class BlockingDrain(BlockingFirstCancelMutationGate owner)
            : ISystemMutationDrain
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    owner.CancelDrain();
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class StubUpdateService : ISystemUpdateService
    {
        public Queue<SystemUpdateStatus>? Statuses { get; set; }

        public Task<SystemUpdateStatus> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Statuses is { Count: > 0 }
                ? Statuses.Dequeue()
                : CurrentUpdateStatus());

        public Task<SystemUpdateStatus> CheckAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<SystemUpdateStatus> ApplyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
