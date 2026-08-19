using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Api.Contracts.SystemMetrics;

public sealed record SystemMetricsDto(
    DateTimeOffset SampledAt,
    HardwareMetricsState State,
    long? HostUptimeSeconds,
    CpuMetricsDto? Cpu,
    MemoryMetricsDto? Memory,
    IReadOnlyList<StorageMetricsDto> Storage,
    IReadOnlyList<GpuMetricsDto> Gpus,
    IReadOnlyList<FanMetricsDto> Fans,
    NetworkMetricsDto? Network,
    IReadOnlyList<HardwareCollectorStatusDto> Collectors)
{
    public static SystemMetricsDto FromSnapshot(HardwareMetricsSnapshot snapshot) => new(
        snapshot.SampledAt,
        snapshot.State,
        snapshot.HostUptimeSeconds,
        snapshot.Cpu is null
            ? null
            : new CpuMetricsDto(
                snapshot.Cpu.UtilizationPercent,
                snapshot.Cpu.TemperatureCelsius,
                snapshot.Cpu.WarningTemperatureCelsius,
                snapshot.Cpu.CriticalTemperatureCelsius,
                snapshot.Cpu.Alarm,
                snapshot.Cpu.Fault),
        snapshot.Memory is null
            ? null
            : new MemoryMetricsDto(
                snapshot.Memory.UsedBytes,
                snapshot.Memory.AvailableBytes,
                snapshot.Memory.TotalBytes,
                snapshot.Memory.UtilizationPercent),
        Array.AsReadOnly(snapshot.Storage.Select(storage => new StorageMetricsDto(
            storage.SourceId,
            storage.Name,
            storage.IsAvailable,
            storage.UsedBytes,
            storage.FreeBytes,
            storage.TotalBytes,
            storage.UtilizationPercent)).ToArray()),
        Array.AsReadOnly(snapshot.Gpus.Select(gpu => new GpuMetricsDto(
            gpu.Id,
            gpu.Vendor,
            gpu.Name,
            gpu.UtilizationPercent,
            gpu.MemoryUsedBytes,
            gpu.MemoryTotalBytes,
            gpu.TemperatureCelsius,
            gpu.WarningTemperatureCelsius,
            gpu.CriticalTemperatureCelsius,
            gpu.Alarm,
            gpu.Fault)).ToArray()),
        Array.AsReadOnly(snapshot.Fans.Select(fan => new FanMetricsDto(
            fan.Id,
            fan.Name,
            fan.RevolutionsPerMinute,
            fan.Alarm,
            fan.Fault)).ToArray()),
        snapshot.Network is null
            ? null
            : new NetworkMetricsDto(
                snapshot.Network.ReceiveBytesPerSecond,
                snapshot.Network.TransmitBytesPerSecond),
        Array.AsReadOnly(snapshot.Collectors.Select(status => new HardwareCollectorStatusDto(
            status.Collector,
            status.State,
            status.Code)).ToArray()));
}

public sealed record CpuMetricsDto(
    double? UtilizationPercent,
    double? TemperatureCelsius,
    double? WarningTemperatureCelsius,
    double? CriticalTemperatureCelsius,
    bool Alarm,
    bool Fault);

public sealed record MemoryMetricsDto(
    long? UsedBytes,
    long? AvailableBytes,
    long? TotalBytes,
    double? UtilizationPercent);

public sealed record StorageMetricsDto(
    string SourceId,
    string Name,
    bool IsAvailable,
    long? UsedBytes,
    long? FreeBytes,
    long? TotalBytes,
    double? UtilizationPercent);

public sealed record GpuMetricsDto(
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

public sealed record FanMetricsDto(
    string Id,
    string Name,
    int? RevolutionsPerMinute,
    bool Alarm,
    bool Fault);

public sealed record NetworkMetricsDto(
    long? ReceiveBytesPerSecond,
    long? TransmitBytesPerSecond);

public sealed record HardwareCollectorStatusDto(
    string Collector,
    HardwareCollectorState State,
    string? Code);
