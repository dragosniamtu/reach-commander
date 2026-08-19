using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Windows;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class WindowsHardwareCollectorTests
{
    [Fact]
    public async Task Collect_maps_cpu_memory_gpu_fan_network_and_uptime_from_safe_nodes()
    {
        using IWindowsSensorSource source = new StubWindowsSensorSource([
            new(WindowsDeviceKind.Cpu, "CPU", WindowsSensorKind.UtilizationPercent, "CPU Total", 25),
            new(WindowsDeviceKind.Cpu, "CPU", WindowsSensorKind.TemperatureCelsius, "CPU Package", 58),
            new(WindowsDeviceKind.Memory, "Memory", WindowsSensorKind.MemoryUsedBytes, "Memory Used", 6_000),
            new(WindowsDeviceKind.Memory, "Memory", WindowsSensorKind.MemoryAvailableBytes, "Memory Available", 10_000),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.UtilizationPercent, "GPU Core", 72),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.MemoryUsedBytes, "GPU Memory Used", 2_000),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.MemoryTotalBytes, "GPU Memory Total", 8_000),
            new(WindowsDeviceKind.GpuNvidia, "RTX Test", WindowsSensorKind.TemperatureCelsius, "GPU Core", 64),
            new(WindowsDeviceKind.Motherboard, "Board", WindowsSensorKind.FanRpm, "CPU Fan", 1400),
            new(WindowsDeviceKind.Network, "Ethernet", WindowsSensorKind.ReceiveBytesPerSecond, "Download", 1000),
            new(WindowsDeviceKind.Network, "Ethernet", WindowsSensorKind.TransmitBytesPerSecond, "Upload", 500),
        ], uptimeSeconds: 3600);
        var collector = CreateCollector(source);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Success, result.Status.State);
        Assert.Equal(25, result.Cpu?.UtilizationPercent);
        Assert.Equal(58, result.Cpu?.TemperatureCelsius);
        Assert.Equal(16_000, result.Memory?.TotalBytes);
        Assert.Equal(37.5, result.Memory?.UtilizationPercent);
        Assert.Equal(72, Assert.Single(result.Gpus!).UtilizationPercent);
        Assert.Equal(1400, Assert.Single(result.Fans!).RevolutionsPerMinute);
        Assert.Equal(1000, result.Network?.ReceiveBytesPerSecond);
        Assert.Equal(3600, result.HostUptimeSeconds);
    }

    [Fact]
    public async Task Collect_sanitizes_labels_and_returns_partial_values_when_sensors_are_missing()
    {
        using IWindowsSensorSource source = new StubWindowsSensorSource([
            new(WindowsDeviceKind.Memory, "Memory", WindowsSensorKind.UtilizationPercent, "Memory", 43),
            new(WindowsDeviceKind.GpuAmd, " GPU\0 Test ", WindowsSensorKind.TemperatureCelsius, "Core", double.NaN),
        ], uptimeSeconds: 1);

        var result = await CreateCollector(source).CollectAsync(CancellationToken.None);

        Assert.Equal(43, result.Memory?.UtilizationPercent);
        Assert.Null(result.Memory?.TotalBytes);
        var gpu = Assert.Single(result.Gpus!);
        Assert.Equal("GPU Test", gpu.Name);
        Assert.Null(gpu.TemperatureCelsius);
    }

    [Fact]
    public async Task Collect_returns_unsupported_without_reading_source_off_windows()
    {
        var source = new StubWindowsSensorSource([], 0);
        var collector = new WindowsHardwareCollector(
            Options.Create(new HardwareMetricsOptions()),
            source,
            StubHostPlatform.Linux);

        var result = await collector.CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Unsupported, result.Status.State);
        Assert.Equal(0, source.ReadCalls);
    }

    private static WindowsHardwareCollector CreateCollector(IWindowsSensorSource source) => new(
        Options.Create(new HardwareMetricsOptions()),
        source,
        StubHostPlatform.Windows);

    private sealed class StubWindowsSensorSource(
        IReadOnlyList<WindowsSensorReading> readings,
        long uptimeSeconds) : IWindowsSensorSource
    {
        public int ReadCalls { get; private set; }

        public IReadOnlyList<WindowsSensorReading> Read()
        {
            ReadCalls++;
            return readings;
        }

        public long GetUptimeSeconds() => uptimeSeconds;

        public void Dispose()
        {
        }
    }
}
