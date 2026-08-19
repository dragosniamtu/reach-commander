namespace ReachCommander.Application.SystemMetrics;

public enum HardwareMetricsState
{
    Healthy,
    Partial,
    Stale,
    Disabled,
}

public enum HardwareCollectorState
{
    Success,
    Unsupported,
    Unavailable,
    Timeout,
    Failed,
}

public sealed record CpuMetrics(
    double? UtilizationPercent,
    double? TemperatureCelsius,
    double? WarningTemperatureCelsius,
    double? CriticalTemperatureCelsius,
    bool Alarm,
    bool Fault);

public sealed record MemoryMetrics(
    long? UsedBytes,
    long? AvailableBytes,
    long? TotalBytes,
    double? UtilizationPercent);

public sealed record StorageMetrics(
    string SourceId,
    string Name,
    bool IsAvailable,
    long? UsedBytes,
    long? FreeBytes,
    long? TotalBytes,
    double? UtilizationPercent);

public sealed record GpuMetrics(
    string Id,
    string Vendor,
    string Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius,
    double? WarningTemperatureCelsius,
    double? CriticalTemperatureCelsius,
    bool Alarm,
    bool Fault);

public sealed record FanMetrics(
    string Id,
    string Name,
    int? RevolutionsPerMinute,
    bool Alarm,
    bool Fault);

public sealed record NetworkMetrics(
    long? ReceiveBytesPerSecond,
    long? TransmitBytesPerSecond);

public sealed record HardwareCollectorStatus(
    string Collector,
    HardwareCollectorState State,
    string? Code);

public sealed record HardwareMetricsSnapshot(
    DateTimeOffset SampledAt,
    HardwareMetricsState State,
    long? HostUptimeSeconds,
    CpuMetrics? Cpu,
    MemoryMetrics? Memory,
    IReadOnlyList<StorageMetrics> Storage,
    IReadOnlyList<GpuMetrics> Gpus,
    IReadOnlyList<FanMetrics> Fans,
    NetworkMetrics? Network,
    IReadOnlyList<HardwareCollectorStatus> Collectors);
