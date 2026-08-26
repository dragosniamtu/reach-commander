using Microsoft.Extensions.Logging.Abstractions;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateOperationMonitorTests
{
    private static readonly DateTimeOffset StartedAt =
        DateTimeOffset.Parse("2026-08-26T10:00:00Z");

    [Fact]
    public async Task Returns_matching_terminal_snapshot()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(
            Applying("operation-1"),
            Terminal("completed", "operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.False(result.TimedOut);
        Assert.Equal("completed", result.TerminalSnapshot!.Phase);
        Assert.Equal(2, gateway.CheckCount);
    }

    [Fact]
    public async Task Retries_transient_unavailability_without_leaking_it_as_a_result()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(
            new SystemUpdaterUnavailableException("/run/private/updater.sock"),
            Terminal("failed", "operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.Equal("failed", result.TerminalSnapshot!.Phase);
        Assert.Equal(2, gateway.CheckCount);
    }

    [Fact]
    public async Task Ignores_terminal_snapshot_for_another_operation()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(
            Terminal("completed", "operation-old"),
            Terminal("rolledBack", "operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.Equal("rolledBack", result.TerminalSnapshot!.Phase);
        Assert.Equal("operation-1", result.TerminalSnapshot.OperationId);
    }

    [Fact]
    public async Task Times_out_six_minutes_after_journal_start()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var gateway = new SequenceGateway(Applying("operation-1"));
        var monitor = CreateMonitor(gateway, clock);

        var result = await monitor.WaitForTerminalAsync(
            Applying("operation-1"), default);

        Assert.True(result.TimedOut);
        Assert.Null(result.TerminalSnapshot);
        Assert.Equal(StartedAt.AddMinutes(6), clock.GetUtcNow());
    }

    [Fact]
    public async Task Propagates_application_shutdown_cancellation()
    {
        var clock = new AdjustableTimeProvider(StartedAt);
        var delay = new BlockingDelay();
        var monitor = new SystemUpdateOperationMonitor(
            new SequenceGateway(Applying("operation-1")),
            delay,
            clock,
            NullLogger<SystemUpdateOperationMonitor>.Instance);
        using var cancellation = new CancellationTokenSource();

        var task = monitor.WaitForTerminalAsync(
            Applying("operation-1"), cancellation.Token);
        await delay.WaitUntilCalledAsync();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static SystemUpdateOperationMonitor CreateMonitor(
        ISystemUpdaterGateway gateway,
        AdjustableTimeProvider clock) => new(
            gateway,
            new AdvancingDelay(clock),
            clock,
            NullLogger<SystemUpdateOperationMonitor>.Instance);

    private static UpdaterSnapshot Applying(string operationId) => new(
        SystemUpdateStatusFactory.ProtocolVersion,
        true,
        "stable",
        "v1.0.3",
        "v1.0.4",
        "applying",
        "update_applying",
        "sanitized",
        operationId,
        StartedAt,
        StartedAt);

    private static UpdaterSnapshot Terminal(string phase, string operationId) =>
        Applying(operationId) with
        {
            Phase = phase,
            ReasonCode = phase switch
            {
                "completed" => "update_completed",
                "rolledBack" => "candidate_rolled_back",
                _ => "update_failed",
            },
            UpdatedAt = StartedAt.AddSeconds(2),
        };

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class AdvancingDelay(AdjustableTimeProvider clock)
        : ISystemUpdateMonitorDelay
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingDelay : ISystemUpdateMonitorDelay
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            _called.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitUntilCalledAsync() =>
            _called.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class SequenceGateway(params object[] responses)
        : ISystemUpdaterGateway
    {
        private readonly Queue<object> _responses = new(responses);
        private object? _last;

        public int CheckCount { get; private set; }

        public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            if (_responses.Count > 0)
            {
                _last = _responses.Dequeue();
            }

            return _last switch
            {
                UpdaterSnapshot snapshot => Task.FromResult(snapshot),
                Exception exception => Task.FromException<UpdaterSnapshot>(exception),
                _ => throw new InvalidOperationException("A response is required."),
            };
        }

        public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
