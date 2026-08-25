using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemMutationGateTests
{
    [Fact]
    public async Task Drain_rejects_new_mutations_and_waits_for_existing_lease()
    {
        var gate = new SystemMutationGate();
        var existing = Assert.IsAssignableFrom<IAsyncDisposable>(gate.TryEnter());

        var drain = gate.BeginDrainAsync(TimeSpan.FromSeconds(1), default);

        Assert.Null(gate.TryEnter());
        Assert.False(drain.IsCompleted);
        await existing.DisposeAsync();
        Assert.True(await drain);
    }

    [Fact]
    public async Task Timed_out_drain_can_be_cancelled_and_leases_are_idempotent()
    {
        var gate = new SystemMutationGate();
        var existing = Assert.IsAssignableFrom<IAsyncDisposable>(gate.TryEnter());

        Assert.False(await gate.BeginDrainAsync(TimeSpan.FromMilliseconds(20), default));
        Assert.Null(gate.TryEnter());
        gate.CancelDrain();

        var next = Assert.IsAssignableFrom<IAsyncDisposable>(gate.TryEnter());
        await existing.DisposeAsync();
        await existing.DisposeAsync();
        await next.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_drain_wait_does_not_reopen_gate_implicitly()
    {
        var gate = new SystemMutationGate();
        await using var existing = Assert.IsAssignableFrom<IAsyncDisposable>(gate.TryEnter());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.BeginDrainAsync(TimeSpan.FromSeconds(1), cancellation.Token));

        Assert.Null(gate.TryEnter());
        gate.CancelDrain();
        Assert.NotNull(gate.TryEnter());
    }
}
