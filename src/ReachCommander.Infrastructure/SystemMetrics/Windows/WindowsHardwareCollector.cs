using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics.Windows;

internal sealed class WindowsHardwareCollector(
    IOptions<HardwareMetricsOptions> options,
    IWindowsSensorSource sensorSource,
    IHostPlatform platform) : IHardwareMetricsCollector
{
    public string Name => "windows-hardware";
    public bool IsSupported => platform.IsWindows;

    public ValueTask<HardwareMetricsContribution> CollectAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return ValueTask.FromResult(HardwareMetricsContribution.Unsupported(Name));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var readings = sensorSource.Read();
            var cpu = MapCpu(readings);
            var memory = MapMemory(readings);
            var gpus = MapGpus(readings);
            var fans = options.Value.FansEnabled ? MapFans(readings) : [];
            var network = options.Value.NetworkEnabled ? MapNetwork(readings) : null;
            var uptime = MetricNormalizer.NonNegative(sensorSource.GetUptimeSeconds());

            return ValueTask.FromResult(new HardwareMetricsContribution(
                new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
                uptime,
                cpu,
                memory,
                Gpus: gpus,
                Fans: fans,
                Network: network));
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            return ValueTask.FromResult(new HardwareMetricsContribution(
                new HardwareCollectorStatus(
                    Name,
                    HardwareCollectorState.Unavailable,
                    "windows_sensors_unavailable")));
        }
    }

    private static CpuMetrics? MapCpu(IReadOnlyList<WindowsSensorReading> readings)
    {
        var cpuReadings = readings.Where(reading => reading.DeviceKind == WindowsDeviceKind.Cpu).ToArray();
        if (cpuReadings.Length == 0)
        {
            return null;
        }

        return new CpuMetrics(
            MetricNormalizer.Percent(First(cpuReadings, WindowsSensorKind.UtilizationPercent)),
            MetricNormalizer.Celsius(First(cpuReadings, WindowsSensorKind.TemperatureCelsius)),
            null,
            null,
            false,
            false);
    }

    private static MemoryMetrics? MapMemory(IReadOnlyList<WindowsSensorReading> readings)
    {
        var memoryReadings = readings
            .Where(reading => reading.DeviceKind == WindowsDeviceKind.Memory)
            .ToArray();
        var used = ToSafeInteger(First(memoryReadings, WindowsSensorKind.MemoryUsedBytes));
        var available = ToSafeInteger(First(memoryReadings, WindowsSensorKind.MemoryAvailableBytes));
        var load = MetricNormalizer.Percent(First(memoryReadings, WindowsSensorKind.UtilizationPercent));

        if (used is not null && available is not null)
        {
            var total = MetricNormalizer.NonNegative(checked(used.Value + available.Value));
            if (total is not null)
            {
                return new MemoryMetrics(
                    used,
                    available,
                    total,
                    MetricNormalizer.Percent(100d * used.Value / total.Value));
            }
        }

        return load is null ? null : new MemoryMetrics(null, null, null, load);
    }

    private static IReadOnlyList<GpuMetrics> MapGpus(IReadOnlyList<WindowsSensorReading> readings)
    {
        var groups = readings
            .Where(reading => IsGpu(reading.DeviceKind))
            .GroupBy(reading => (reading.DeviceKind, reading.DeviceName))
            .Take(MetricNormalizer.MaximumGpuCount);
        var gpus = new List<GpuMetrics>(MetricNormalizer.MaximumGpuCount);
        foreach (var group in groups)
        {
            var groupReadings = group.ToArray();
            var used = ToSafeInteger(First(groupReadings, WindowsSensorKind.MemoryUsedBytes));
            var total = ToSafeInteger(First(groupReadings, WindowsSensorKind.MemoryTotalBytes));
            var available = ToSafeInteger(First(groupReadings, WindowsSensorKind.MemoryAvailableBytes));
            if (used is null && total is not null && available is not null && available <= total)
            {
                used = total - available;
            }

            if (used is not null && total is not null && used > total)
            {
                used = null;
            }

            var ordinal = gpus.Count + 1;
            var (vendorId, vendorName) = Vendor(group.Key.DeviceKind);
            gpus.Add(new GpuMetrics(
                $"gpu-{vendorId}-{ordinal:D3}",
                vendorName,
                MetricNormalizer.Label(group.Key.DeviceName, $"{vendorName} GPU {ordinal}"),
                MetricNormalizer.Percent(First(groupReadings, WindowsSensorKind.UtilizationPercent)),
                used,
                total,
                MetricNormalizer.Celsius(First(groupReadings, WindowsSensorKind.TemperatureCelsius)),
                null,
                null,
                false,
                false));
        }

        return gpus.AsReadOnly();
    }

    private static IReadOnlyList<FanMetrics> MapFans(IReadOnlyList<WindowsSensorReading> readings)
    {
        var fans = readings
            .Where(reading => reading.SensorKind == WindowsSensorKind.FanRpm)
            .Take(MetricNormalizer.MaximumSensorsPerFamily)
            .Select((reading, index) => new FanMetrics(
                $"fan-{index + 1:D3}",
                MetricNormalizer.Label(reading.SensorName, $"Fan {index + 1}"),
                ToRpm(reading.Value),
                false,
                false))
            .ToArray();
        return Array.AsReadOnly(fans);
    }

    private static NetworkMetrics? MapNetwork(IReadOnlyList<WindowsSensorReading> readings)
    {
        var receive = Sum(readings, WindowsSensorKind.ReceiveBytesPerSecond);
        var transmit = Sum(readings, WindowsSensorKind.TransmitBytesPerSecond);
        return receive is null && transmit is null ? null : new NetworkMetrics(receive, transmit);
    }

    private static long? Sum(
        IReadOnlyList<WindowsSensorReading> readings,
        WindowsSensorKind kind)
    {
        var values = readings.Where(reading => reading.SensorKind == kind).ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        double total = 0;
        foreach (var reading in values)
        {
            if (!double.IsFinite(reading.Value) || reading.Value < 0)
            {
                continue;
            }

            total += reading.Value;
            if (!double.IsFinite(total) || total > MetricNormalizer.MaximumSafeJsonInteger)
            {
                return null;
            }
        }

        return checked((long)Math.Round(total, MidpointRounding.AwayFromZero));
    }

    private static double? First(
        IReadOnlyList<WindowsSensorReading> readings,
        WindowsSensorKind kind) => readings.FirstOrDefault(reading => reading.SensorKind == kind)?.Value;

    private static long? ToSafeInteger(double? value) =>
        value is not null &&
        double.IsFinite(value.Value) &&
        value is >= 0 and <= MetricNormalizer.MaximumSafeJsonInteger
            ? checked((long)Math.Round(value.Value, MidpointRounding.AwayFromZero))
            : null;

    private static int? ToRpm(double value) =>
        double.IsFinite(value) && value is >= 0 and <= int.MaxValue
            ? checked((int)Math.Round(value, MidpointRounding.AwayFromZero))
            : null;

    private static bool IsGpu(WindowsDeviceKind kind) => kind is
        WindowsDeviceKind.GpuNvidia or WindowsDeviceKind.GpuAmd or WindowsDeviceKind.GpuIntel;

    private static (string Id, string Name) Vendor(WindowsDeviceKind kind) => kind switch
    {
        WindowsDeviceKind.GpuNvidia => ("nvidia", "NVIDIA"),
        WindowsDeviceKind.GpuAmd => ("amd", "AMD"),
        WindowsDeviceKind.GpuIntel => ("intel", "Intel"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
