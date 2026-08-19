using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal interface IHardwareCollectorRunner
{
    ValueTask<HardwareMetricsContribution> RunAsync(
        IHardwareMetricsCollector collector,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal interface IHardwareMetricsDelay
{
    Task DelayAsync(
        TimeSpan interval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);
}

internal sealed class HardwareMetricsDelay : IHardwareMetricsDelay
{
    public Task DelayAsync(
        TimeSpan interval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        Task.Delay(interval, timeProvider, cancellationToken);
}

internal sealed class BoundedHardwareCollectorRunner(
    ILogger<BoundedHardwareCollectorRunner> logger) : IHardwareCollectorRunner
{
    private readonly ConcurrentDictionary<IHardwareMetricsCollector, Task<HardwareMetricsContribution>> _inFlight =
        new(ReferenceEqualityComparer.Instance);

    public async ValueTask<HardwareMetricsContribution> RunAsync(
        IHardwareMetricsCollector collector,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_inFlight.TryGetValue(collector, out var completed) && completed.IsCompleted)
        {
            _inFlight.TryRemove(
                new KeyValuePair<IHardwareMetricsCollector, Task<HardwareMetricsContribution>>(
                    collector,
                    completed));
        }

        var task = _inFlight.GetOrAdd(
            collector,
            candidate => StartCollection(candidate, cancellationToken));
        try
        {
            return await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Result(collector.Name, HardwareCollectorState.Timeout, "collector_timeout");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            logger.LogWarning(
                "Hardware collector {Collector} ended with state {State} and code {Code}.",
                collector.Name,
                HardwareCollectorState.Unavailable,
                "collector_unavailable");
            return Result(collector.Name, HardwareCollectorState.Unavailable, "collector_unavailable");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(
                "Hardware collector {Collector} ended with state {State} and code {Code}.",
                collector.Name,
                HardwareCollectorState.Failed,
                "collector_failed");
            return Result(collector.Name, HardwareCollectorState.Failed, "collector_failed");
        }
    }

    private Task<HardwareMetricsContribution> StartCollection(
        IHardwareMetricsCollector collector,
        CancellationToken cancellationToken)
    {
        var task = Task.Run(
            async () => await collector.CollectAsync(cancellationToken).ConfigureAwait(false),
            CancellationToken.None);

        _ = task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                    logger.LogWarning(
                        "Late hardware collector {Collector} fault observed with code {Code}.",
                        collector.Name,
                        "collector_late_fault");
                }

                _inFlight.TryRemove(
                    new KeyValuePair<IHardwareMetricsCollector, Task<HardwareMetricsContribution>>(
                        collector,
                        completed));
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return task;
    }

    private static bool IsUnavailable(Exception exception) => exception is
        UnauthorizedAccessException or
        SecurityException or
        IOException or
        DllNotFoundException or
        EntryPointNotFoundException or
        BadImageFormatException;

    private static HardwareMetricsContribution Result(
        string collector,
        HardwareCollectorState state,
        string code) => new(new HardwareCollectorStatus(collector, state, code));
}

internal sealed class HardwareMetricsSampler(
    IEnumerable<IHardwareMetricsCollector> collectors,
    HardwareMetricsSnapshotCache cache,
    IHardwareCollectorRunner runner,
    IHardwareMetricsDelay delay,
    IOptions<HardwareMetricsOptions> options,
    TimeProvider timeProvider,
    ILogger<HardwareMetricsSampler> logger) : BackgroundService
{
    private readonly IHardwareMetricsCollector[] _collectors = collectors.ToArray();
    private readonly SemaphoreSlim _sampleGate = new(1, 1);

    internal async Task SampleOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _sampleGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var attemptTime = timeProvider.GetUtcNow();
            if (!options.Value.Enabled)
            {
                cache.Set(EmptySnapshot(attemptTime, HardwareMetricsState.Disabled));
                return;
            }

            var correlationId = Guid.NewGuid();
            var timeout = TimeSpan.FromMilliseconds(options.Value.CollectorTimeoutMilliseconds);
            var collectionTasks = _collectors
                .Select(collector => CollectAsync(collector, correlationId, timeout, cancellationToken))
                .ToArray();
            var contributions = await Task.WhenAll(collectionTasks).ConfigureAwait(false);

            HardwareMetricsSnapshot? previous = null;
            try
            {
                previous = cache.GetCurrent();
            }
            catch (HardwareMetricsNotReadyException)
            {
                // The first enabled attempt has no prior families to retain.
            }

            cache.Set(Merge(attemptTime, contributions, previous));
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await SampleOnceAsync(stoppingToken).ConfigureAwait(false);
                await delay.DelayAsync(
                    TimeSpan.FromSeconds(options.Value.SampleIntervalSeconds),
                    timeProvider,
                    stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    private async Task<HardwareMetricsContribution> CollectAsync(
        IHardwareMetricsCollector collector,
        Guid correlationId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!collector.IsSupported)
        {
            return HardwareMetricsContribution.Unsupported(collector.Name);
        }

        var started = Stopwatch.GetTimestamp();
        var contribution = await runner.RunAsync(collector, timeout, cancellationToken).ConfigureAwait(false);
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Hardware metrics sample {CorrelationId}: collector {Collector} state {State} elapsed {ElapsedMilliseconds:F0} ms code {Code}.",
            correlationId,
            contribution.Status.Collector,
            contribution.Status.State,
            elapsedMilliseconds,
            contribution.Status.Code);
        return contribution;
    }

    private static HardwareMetricsSnapshot Merge(
        DateTimeOffset attemptTime,
        IReadOnlyList<HardwareMetricsContribution> contributions,
        HardwareMetricsSnapshot? previous)
    {
        var currentCpu = MergeCpu(contributions.Select(contribution => contribution.Cpu));
        var currentMemory = contributions.Select(contribution => contribution.Memory).FirstOrDefault(IsUsable);
        var currentUptime = contributions.Select(contribution => contribution.HostUptimeSeconds).FirstOrDefault(value => value is not null);
        var currentNetwork = contributions.Select(contribution => contribution.Network).FirstOrDefault(value => value is not null);

        var storageSupplied = contributions.Any(contribution => contribution.Storage is not null);
        var gpusSupplied = contributions.Any(contribution => contribution.Gpus is not null);
        var fansSupplied = contributions.Any(contribution => contribution.Fans is not null);
        var currentStorage = BoundStorage(contributions.SelectMany(contribution => contribution.Storage ?? []));
        var currentGpus = BoundGpus(contributions.SelectMany(contribution => contribution.Gpus ?? []));
        var currentFans = BoundFans(contributions.SelectMany(contribution => contribution.Fans ?? []));

        var hasRequiredCurrentData =
            currentCpu?.UtilizationPercent is not null &&
            currentMemory is not null &&
            storageSupplied &&
            currentStorage.Count > 0;
        var hasApplicableFailure = contributions.Any(contribution =>
            contribution.Status.State is HardwareCollectorState.Unavailable or
                HardwareCollectorState.Timeout or
                HardwareCollectorState.Failed);

        var sampledAt = previous is null || hasRequiredCurrentData
            ? attemptTime
            : previous.SampledAt;
        var state = hasRequiredCurrentData && !hasApplicableFailure
            ? HardwareMetricsState.Healthy
            : HardwareMetricsState.Partial;

        var cpu = MergeCpu([currentCpu, previous?.Cpu]);
        var memory = currentMemory ?? previous?.Memory;
        var storage = storageSupplied ? currentStorage : previous?.Storage ?? [];
        var gpus = gpusSupplied ? currentGpus : previous?.Gpus ?? [];
        var fans = fansSupplied ? currentFans : previous?.Fans ?? [];
        var statuses = contributions.Select(contribution => contribution.Status).ToArray();

        return new HardwareMetricsSnapshot(
            sampledAt,
            state,
            currentUptime ?? previous?.HostUptimeSeconds,
            cpu,
            memory,
            storage,
            gpus,
            fans,
            currentNetwork ?? previous?.Network,
            Array.AsReadOnly(statuses));
    }

    private static CpuMetrics? MergeCpu(IEnumerable<CpuMetrics?> candidates)
    {
        var present = candidates.Where(candidate => candidate is not null).Cast<CpuMetrics>().ToArray();
        if (present.Length == 0)
        {
            return null;
        }

        return new CpuMetrics(
            present.Select(cpu => cpu.UtilizationPercent).FirstOrDefault(value => value is not null),
            present.Select(cpu => cpu.TemperatureCelsius).FirstOrDefault(value => value is not null),
            present.Select(cpu => cpu.WarningTemperatureCelsius).FirstOrDefault(value => value is not null),
            present.Select(cpu => cpu.CriticalTemperatureCelsius).FirstOrDefault(value => value is not null),
            present.Any(cpu => cpu.Alarm),
            present.Any(cpu => cpu.Fault));
    }

    private static bool IsUsable(MemoryMetrics? memory) => memory is not null &&
        (memory.UsedBytes is not null || memory.AvailableBytes is not null ||
         memory.TotalBytes is not null || memory.UtilizationPercent is not null);

    private static IReadOnlyList<StorageMetrics> BoundStorage(IEnumerable<StorageMetrics> storage) =>
        Array.AsReadOnly(storage.Take(MetricNormalizer.MaximumSensorsPerFamily).ToArray());

    private static IReadOnlyList<GpuMetrics> BoundGpus(IEnumerable<GpuMetrics> gpus) =>
        Array.AsReadOnly(gpus
            .DistinctBy(gpu => gpu.Id, StringComparer.Ordinal)
            .Take(MetricNormalizer.MaximumGpuCount)
            .ToArray());

    private static IReadOnlyList<FanMetrics> BoundFans(IEnumerable<FanMetrics> fans) =>
        Array.AsReadOnly(fans
            .DistinctBy(fan => (fan.Name, fan.RevolutionsPerMinute))
            .Take(MetricNormalizer.MaximumSensorsPerFamily)
            .ToArray());

    private static HardwareMetricsSnapshot EmptySnapshot(
        DateTimeOffset sampledAt,
        HardwareMetricsState state) => new(
        sampledAt,
        state,
        null,
        null,
        null,
        [],
        [],
        [],
        null,
        []);
}
