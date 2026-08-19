using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class HardwareMetricsSamplerTests
{
    [Fact]
    public void Get_current_before_first_sample_throws_not_ready()
    {
        var cache = CreateCache(new ManualMetricsTimeProvider(DateTimeOffset.UtcNow));

        Assert.Throws<HardwareMetricsNotReadyException>(() => cache.GetCurrent());
    }

    [Fact]
    public async Task Sample_merges_complementary_cpu_fields_and_marks_failed_applicable_collector_partial()
    {
        var clock = new ManualMetricsTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(clock);
        IHardwareMetricsCollector[] collectors =
        [
            Collector("linux-proc", new HardwareMetricsContribution(
                Status("linux-proc"), 99,
                new CpuMetrics(25, null, null, null, false, false),
                new MemoryMetrics(60, 40, 100, 60),
                Network: new NetworkMetrics(1000, 500))),
            Collector("linux-hwmon", new HardwareMetricsContribution(
                Status("linux-hwmon"),
                Cpu: new CpuMetrics(null, 55, 90, 100, false, false))),
            Collector("gpu", new HardwareMetricsContribution(
                new HardwareCollectorStatus("gpu", HardwareCollectorState.Unavailable, "gpu_access_denied"))),
        ];
        var sampler = CreateSampler(collectors, cache, clock, new ImmediateCollectorRunner());

        await sampler.SampleOnceAsync(CancellationToken.None);
        var snapshot = cache.GetCurrent();

        Assert.Equal(HardwareMetricsState.Partial, snapshot.State);
        Assert.Equal(25, snapshot.Cpu?.UtilizationPercent);
        Assert.Equal(55, snapshot.Cpu?.TemperatureCelsius);
        Assert.Equal(99, snapshot.HostUptimeSeconds);
    }

    [Fact]
    public void Cache_marks_last_good_snapshot_stale_after_fifteen_seconds()
    {
        var clock = new ManualMetricsTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(clock);
        cache.Set(Snapshot(clock.GetUtcNow()));

        clock.Advance(TimeSpan.FromSeconds(16));

        Assert.Equal(HardwareMetricsState.Stale, cache.GetCurrent().State);
    }

    [Fact]
    public async Task Disabled_sampler_publishes_disabled_snapshot_without_invoking_collectors()
    {
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCache(clock, enabled: false);
        var collector = new CountingCollector();
        var sampler = CreateSampler([collector], cache, clock, new ImmediateCollectorRunner(), enabled: false);

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(0, collector.CallCount);
        Assert.Equal(HardwareMetricsState.Disabled, cache.GetCurrent().State);
    }

    [Fact]
    public async Task Timeout_does_not_remove_another_collectors_data()
    {
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCache(clock);
        var good = Collector("good", new HardwareMetricsContribution(
            Status("good"), Cpu: new CpuMetrics(10, null, null, null, false, false)));
        var slow = Collector("slow", new HardwareMetricsContribution(Status("slow")));
        var sampler = CreateSampler([good, slow], cache, clock, new FakeTimeoutCollectorRunner("slow"));

        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(10, cache.GetCurrent().Cpu?.UtilizationPercent);
        Assert.Contains(cache.GetCurrent().Collectors,
            status => status.Collector == "slow" && status.State == HardwareCollectorState.Timeout);
    }

    [Fact]
    public async Task Transient_failure_preserves_family_then_recovery_replaces_it()
    {
        var clock = new ManualMetricsTimeProvider(new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero));
        var cache = CreateCache(clock);
        var cpuAndMemory = new MutableCollector("base", BaseContribution(10));
        var storage = Collector("storage", StorageContribution());
        var sampler = CreateSampler([cpuAndMemory, storage], cache, clock, new ImmediateCollectorRunner());

        await sampler.SampleOnceAsync(CancellationToken.None);
        var first = cache.GetCurrent();
        Assert.Equal(HardwareMetricsState.Healthy, first.State);

        clock.Advance(TimeSpan.FromSeconds(5));
        cpuAndMemory.Contribution = new HardwareMetricsContribution(
            new HardwareCollectorStatus("base", HardwareCollectorState.Failed, "base_unavailable"));
        await sampler.SampleOnceAsync(CancellationToken.None);
        var failed = cache.GetCurrent();

        Assert.Equal(HardwareMetricsState.Partial, failed.State);
        Assert.Equal(10, failed.Cpu?.UtilizationPercent);
        Assert.Equal(first.SampledAt, failed.SampledAt);

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HardwareMetricsState.Stale, cache.GetCurrent().State);

        cpuAndMemory.Contribution = BaseContribution(20);
        await sampler.SampleOnceAsync(CancellationToken.None);
        var recovered = cache.GetCurrent();

        Assert.Equal(HardwareMetricsState.Healthy, recovered.State);
        Assert.Equal(20, recovered.Cpu?.UtilizationPercent);
        Assert.True(recovered.SampledAt > first.SampledAt);
    }

    [Fact]
    public async Task Concurrent_sample_request_is_skipped()
    {
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCache(clock);
        var collector = new GatedCollector();
        var sampler = CreateSampler([collector], cache, clock, new ImmediateCollectorRunner());

        var first = sampler.SampleOnceAsync(CancellationToken.None);
        await collector.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await sampler.SampleOnceAsync(CancellationToken.None);

        Assert.Equal(1, collector.CallCount);
        collector.Release(BaseContribution(1));
        await first;
    }

    [Fact]
    public async Task Hosted_loop_samples_immediately_then_requests_exact_five_second_delay()
    {
        var clock = new ManualMetricsTimeProvider(DateTimeOffset.UtcNow);
        var cache = CreateCache(clock);
        var collector = new CountingCollector();
        var delay = new GatedMetricsDelay();
        var sampler = CreateSampler([collector], cache, clock, new ImmediateCollectorRunner(), delay: delay);

        await sampler.StartAsync(CancellationToken.None);
        var requested = await delay.FirstRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(5), requested);
        Assert.Equal(1, collector.CallCount);

        delay.ReleaseOnce();
        await delay.SecondRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, collector.CallCount);

        await sampler.StopAsync(CancellationToken.None);
    }

    private static HardwareMetricsSnapshotCache CreateCache(TimeProvider clock, bool enabled = true) =>
        new(Options.Create(new HardwareMetricsOptions { Enabled = enabled }), clock);

    private static HardwareMetricsSampler CreateSampler(
        IEnumerable<IHardwareMetricsCollector> collectors,
        HardwareMetricsSnapshotCache cache,
        TimeProvider clock,
        IHardwareCollectorRunner runner,
        bool enabled = true,
        IHardwareMetricsDelay? delay = null) => new(
            collectors,
            cache,
            runner,
            delay ?? new BlockingMetricsDelay(),
            Options.Create(new HardwareMetricsOptions { Enabled = enabled }),
            clock,
            NullLogger<HardwareMetricsSampler>.Instance);

    private static IHardwareMetricsCollector Collector(string name, HardwareMetricsContribution contribution) =>
        new FixedCollector(name, contribution);

    private static HardwareCollectorStatus Status(string name) =>
        new(name, HardwareCollectorState.Success, null);

    private static HardwareMetricsContribution BaseContribution(double utilization) => new(
        Status("base"),
        HostUptimeSeconds: 1,
        Cpu: new CpuMetrics(utilization, null, null, null, false, false),
        Memory: new MemoryMetrics(60, 40, 100, 60));

    private static HardwareMetricsContribution StorageContribution() => new(
        Status("storage"),
        Storage: [new StorageMetrics("source", "Source", true, 50, 50, 100, 50)]);

    private static HardwareMetricsSnapshot Snapshot(DateTimeOffset sampledAt) => new(
        sampledAt, HardwareMetricsState.Healthy, 1, null, null, [], [], [], null, []);

    private sealed class FixedCollector(string name, HardwareMetricsContribution contribution) : IHardwareMetricsCollector
    {
        public string Name => name;
        public bool IsSupported => true;
        public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(contribution);
    }

    private sealed class MutableCollector(string name, HardwareMetricsContribution contribution) : IHardwareMetricsCollector
    {
        public string Name => name;
        public bool IsSupported => true;
        public HardwareMetricsContribution Contribution { get; set; } = contribution;
        public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Contribution);
    }

    private sealed class CountingCollector : IHardwareMetricsCollector
    {
        public int CallCount { get; private set; }
        public string Name => "counting";
        public bool IsSupported => true;

        public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(new HardwareMetricsContribution(Status(Name)));
        }
    }

    private sealed class GatedCollector : IHardwareMetricsCollector
    {
        private readonly TaskCompletionSource<HardwareMetricsContribution> _result =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "gated";
        public bool IsSupported => true;

        public async ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            return await _result.Task.WaitAsync(cancellationToken);
        }

        public void Release(HardwareMetricsContribution contribution) => _result.TrySetResult(contribution);
    }

    private sealed class ImmediateCollectorRunner : IHardwareCollectorRunner
    {
        public ValueTask<HardwareMetricsContribution> RunAsync(
            IHardwareMetricsCollector collector,
            TimeSpan timeout,
            CancellationToken cancellationToken) => collector.CollectAsync(cancellationToken);
    }

    private sealed class FakeTimeoutCollectorRunner(string timedOutCollector) : IHardwareCollectorRunner
    {
        public ValueTask<HardwareMetricsContribution> RunAsync(
            IHardwareMetricsCollector collector,
            TimeSpan timeout,
            CancellationToken cancellationToken) => collector.Name == timedOutCollector
                ? ValueTask.FromResult(new HardwareMetricsContribution(
                    new HardwareCollectorStatus(collector.Name, HardwareCollectorState.Timeout, "collector_timeout")))
                : collector.CollectAsync(cancellationToken);
    }

    private sealed class BlockingMetricsDelay : IHardwareMetricsDelay
    {
        public Task DelayAsync(TimeSpan interval, TimeProvider timeProvider, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class GatedMetricsDelay : IHardwareMetricsDelay
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public TaskCompletionSource<TimeSpan> FirstRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<TimeSpan> SecondRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task DelayAsync(TimeSpan interval, TimeProvider timeProvider, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            (call == 1 ? FirstRequested : SecondRequested).TrySetResult(interval);
            if (call == 1)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public void ReleaseOnce() => _release.TrySetResult();
    }
}
