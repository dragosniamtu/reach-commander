namespace ReachCommander.Infrastructure.SystemMetrics;

internal interface IHardwareMetricsCollector
{
    string Name { get; }
    bool IsSupported { get; }
    ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken);
}
