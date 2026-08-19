using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed record HardwareMetricsContribution(
    HardwareCollectorStatus Status,
    long? HostUptimeSeconds = null,
    CpuMetrics? Cpu = null,
    MemoryMetrics? Memory = null,
    IReadOnlyList<StorageMetrics>? Storage = null,
    IReadOnlyList<GpuMetrics>? Gpus = null,
    IReadOnlyList<FanMetrics>? Fans = null,
    NetworkMetrics? Network = null)
{
    public static HardwareMetricsContribution Unsupported(string collector) => new(
        new HardwareCollectorStatus(
            collector,
            HardwareCollectorState.Unsupported,
            "collector_unsupported"));
}
