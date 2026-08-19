using Microsoft.Extensions.Logging.Abstractions;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class BoundedHardwareCollectorRunnerTests
{
    [Fact]
    public async Task Timed_out_calls_share_one_in_flight_collection_then_restart_after_completion()
    {
        var collector = new GatedCollector();
        var runner = CreateRunner();

        var first = await runner.RunAsync(collector, TimeSpan.Zero, CancellationToken.None);
        var second = await runner.RunAsync(collector, TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Timeout, first.Status.State);
        Assert.Equal(HardwareCollectorState.Timeout, second.Status.State);
        await collector.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, collector.CallCount);

        collector.ReleaseFirst();
        await collector.FirstFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

        HardwareMetricsContribution? restarted = null;
        for (var attempt = 0; attempt < 1000 && collector.CallCount == 1; attempt++)
        {
            restarted = await runner.RunAsync(collector, TimeSpan.Zero, CancellationToken.None);
            await Task.Yield();
        }

        await collector.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, collector.CallCount);
        Assert.Equal(HardwareCollectorState.Timeout, restarted?.Status.State);
        collector.ReleaseSecond();
    }

    [Fact]
    public async Task Late_fault_is_observed_and_does_not_escape_on_next_call()
    {
        var collector = new LateFaultCollector();
        var logger = new RecordingLogger();
        var runner = new BoundedHardwareCollectorRunner(logger);

        var timeout = await runner.RunAsync(collector, TimeSpan.Zero, CancellationToken.None);
        Assert.Equal(HardwareCollectorState.Timeout, timeout.Status.State);
        await collector.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        collector.Fail();
        await logger.LateFaultObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var next = await runner.RunAsync(collector, TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Success, next.Status.State);
        Assert.Equal(2, collector.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_before_start_does_not_invoke_collector()
    {
        var collector = new CountingCollector();
        var runner = CreateRunner();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await runner.RunAsync(collector, TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Equal(0, collector.CallCount);
    }

    private static BoundedHardwareCollectorRunner CreateRunner() =>
        new(NullLogger<BoundedHardwareCollectorRunner>.Instance);

    private sealed class GatedCollector : IHardwareMetricsCollector
    {
        private readonly TaskCompletionSource _firstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "gated";
        public bool IsSupported => true;

        public async ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
        {
            var call = ++CallCount;
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await _firstGate.Task;
                FirstFinished.TrySetResult();
            }
            else
            {
                SecondStarted.TrySetResult();
                await _secondGate.Task;
            }

            return Success(Name);
        }

        public void ReleaseFirst() => _firstGate.TrySetResult();
        public void ReleaseSecond() => _secondGate.TrySetResult();
    }

    private sealed class LateFaultCollector : IHardwareMetricsCollector
    {
        private readonly TaskCompletionSource _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "late-fault";
        public bool IsSupported => true;

        public async ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                Started.TrySetResult();
                await _failure.Task;
                throw new InvalidOperationException("unreachable");
            }

            return Success(Name);
        }

        public void Fail() => _failure.TrySetException(new IOException("late collector failure"));
    }

    private sealed class CountingCollector : IHardwareMetricsCollector
    {
        public int CallCount { get; private set; }
        public string Name => "counting";
        public bool IsSupported => true;

        public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(Success(Name));
        }
    }

    private static HardwareMetricsContribution Success(string name) => new(
        new HardwareCollectorStatus(name, HardwareCollectorState.Success, null));

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<BoundedHardwareCollectorRunner>
    {
        public TaskCompletionSource LateFaultObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("collector_late_fault", StringComparison.Ordinal))
            {
                LateFaultObserved.TrySetResult();
            }
        }
    }
}
