using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.SystemMetrics.Windows;

internal enum WindowsDeviceKind
{
    Cpu,
    Memory,
    Motherboard,
    Controller,
    Network,
    GpuNvidia,
    GpuAmd,
    GpuIntel,
}

internal enum WindowsSensorKind
{
    UtilizationPercent,
    TemperatureCelsius,
    MemoryUsedBytes,
    MemoryAvailableBytes,
    MemoryTotalBytes,
    FanRpm,
    ReceiveBytesPerSecond,
    TransmitBytesPerSecond,
}

internal sealed record WindowsSensorReading(
    WindowsDeviceKind DeviceKind,
    string DeviceName,
    WindowsSensorKind SensorKind,
    string SensorName,
    double Value);

internal interface IWindowsSensorSource : IDisposable
{
    IReadOnlyList<WindowsSensorReading> Read();
    long GetUptimeSeconds();
}

internal enum WindowsRawSensorKind
{
    Load,
    Temperature,
    Data,
    SmallData,
    Fan,
    Throughput,
    Power,
    Other,
}

internal sealed record WindowsRawSensor(
    WindowsRawSensorKind Kind,
    string Name,
    double? Value);

internal sealed record WindowsRawDevice(
    WindowsDeviceKind Kind,
    string Name,
    string InternalIdentifier,
    IReadOnlyList<WindowsRawSensor> Sensors);

internal interface ILibreHardwareSession : IDisposable
{
    void Open();
    void Update();
    IReadOnlyList<WindowsRawDevice> ReadDevices();
}

internal sealed class LibreHardwareSession : ILibreHardwareSession
{
    private readonly Computer _computer = new()
    {
        IsControllerEnabled = true,
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsNetworkEnabled = true,
    };
    private bool _opened;

    public void Open()
    {
        if (_opened)
        {
            return;
        }

        _computer.Open();
        _opened = true;
    }

    public void Update()
    {
        foreach (var hardware in _computer.Hardware)
        {
            UpdateTree(hardware);
        }
    }

    public IReadOnlyList<WindowsRawDevice> ReadDevices()
    {
        var devices = new List<WindowsRawDevice>(MetricNormalizer.MaximumSensorsPerFamily);
        foreach (var hardware in _computer.Hardware)
        {
            ReadTree(hardware, devices);
            if (devices.Count >= MetricNormalizer.MaximumSensorsPerFamily)
            {
                break;
            }
        }

        return devices.AsReadOnly();
    }

    public void Dispose()
    {
        if (!_opened)
        {
            return;
        }

        _computer.Close();
        _opened = false;
    }

    private static void UpdateTree(IHardware hardware)
    {
        hardware.Update();
        foreach (var subHardware in hardware.SubHardware)
        {
            UpdateTree(subHardware);
        }
    }

    private static void ReadTree(IHardware hardware, ICollection<WindowsRawDevice> devices)
    {
        var kind = MapDeviceKind(hardware.HardwareType);
        if (kind is not null && devices.Count < MetricNormalizer.MaximumSensorsPerFamily)
        {
            var sensors = hardware.Sensors
                .Take(MetricNormalizer.MaximumSensorsPerFamily)
                .Select(sensor => new WindowsRawSensor(
                    MapSensorKind(sensor.SensorType),
                    sensor.Name,
                    sensor.Value))
                .ToArray();
            devices.Add(new WindowsRawDevice(
                kind.Value,
                hardware.Name,
                hardware.Identifier.ToString(),
                Array.AsReadOnly(sensors)));
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            ReadTree(subHardware, devices);
            if (devices.Count >= MetricNormalizer.MaximumSensorsPerFamily)
            {
                break;
            }
        }
    }

    private static WindowsDeviceKind? MapDeviceKind(HardwareType kind) => kind switch
    {
        HardwareType.Cpu => WindowsDeviceKind.Cpu,
        HardwareType.Memory => WindowsDeviceKind.Memory,
        HardwareType.Motherboard => WindowsDeviceKind.Motherboard,
        HardwareType.SuperIO or HardwareType.Cooler or HardwareType.EmbeddedController =>
            WindowsDeviceKind.Controller,
        HardwareType.Network => WindowsDeviceKind.Network,
        HardwareType.GpuNvidia => WindowsDeviceKind.GpuNvidia,
        HardwareType.GpuAmd => WindowsDeviceKind.GpuAmd,
        HardwareType.GpuIntel => WindowsDeviceKind.GpuIntel,
        _ => null,
    };

    private static WindowsRawSensorKind MapSensorKind(SensorType kind) => kind switch
    {
        SensorType.Load => WindowsRawSensorKind.Load,
        SensorType.Temperature => WindowsRawSensorKind.Temperature,
        SensorType.Data => WindowsRawSensorKind.Data,
        SensorType.SmallData => WindowsRawSensorKind.SmallData,
        SensorType.Fan => WindowsRawSensorKind.Fan,
        SensorType.Throughput => WindowsRawSensorKind.Throughput,
        SensorType.Power => WindowsRawSensorKind.Power,
        _ => WindowsRawSensorKind.Other,
    };
}

internal sealed class LibreHardwareMonitorAdapter(
    ILibreHardwareSession session,
    IHostPlatform platform,
    IOptions<HardwareMetricsOptions> options,
    ILogger<LibreHardwareMonitorAdapter> logger) : IWindowsSensorSource
{
    private const int MaximumReadings = 512;
    private readonly object _gate = new();
    private bool _opened;
    private bool _disposed;

    public IReadOnlyList<WindowsSensorReading> Read()
    {
        if (!platform.IsWindows || Volatile.Read(ref _disposed))
        {
            return [];
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            if (!_opened)
            {
                session.Open();
                _opened = true;
            }

            session.Update();
            return MapReadings(session.ReadDevices());
        }
    }

    public long GetUptimeSeconds() => Math.Max(0, Environment.TickCount64 / 1000);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        if (!Monitor.TryEnter(_gate, options.Value.CollectorTimeoutMilliseconds))
        {
            logger.LogWarning("Windows hardware session disposal deferred because collection is still active.");
            return;
        }

        try
        {
            session.Dispose();
            _opened = false;
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private static IReadOnlyList<WindowsSensorReading> MapReadings(
        IReadOnlyList<WindowsRawDevice> devices)
    {
        var readings = new List<WindowsSensorReading>(Math.Min(MaximumReadings, devices.Count * 4));
        foreach (var device in devices.Take(MetricNormalizer.MaximumSensorsPerFamily))
        {
            switch (device.Kind)
            {
                case WindowsDeviceKind.Cpu:
                    MapCpu(device, readings);
                    break;
                case WindowsDeviceKind.Memory:
                    MapMemory(device, readings);
                    break;
                case WindowsDeviceKind.GpuNvidia:
                case WindowsDeviceKind.GpuAmd:
                case WindowsDeviceKind.GpuIntel:
                    MapGpu(device, readings);
                    MapFans(device, readings);
                    break;
                case WindowsDeviceKind.Motherboard:
                case WindowsDeviceKind.Controller:
                    MapFans(device, readings);
                    break;
                case WindowsDeviceKind.Network:
                    MapNetwork(device, readings);
                    break;
            }

            if (readings.Count >= MaximumReadings)
            {
                break;
            }
        }

        return readings.Take(MaximumReadings).ToArray();
    }

    private static void MapCpu(WindowsRawDevice device, ICollection<WindowsSensorReading> readings)
    {
        var load = device.Sensors.FirstOrDefault(sensor =>
                sensor.Kind == WindowsRawSensorKind.Load &&
                sensor.Name.Equals("CPU Total", StringComparison.OrdinalIgnoreCase))
            ?? device.Sensors.FirstOrDefault(sensor => sensor.Kind == WindowsRawSensorKind.Load);
        Add(readings, device, load, WindowsSensorKind.UtilizationPercent, Identity);

        var temperature = SelectByName(
            device.Sensors.Where(sensor => sensor.Kind == WindowsRawSensorKind.Temperature),
            ["CPU Package", "Package", "Tctl", "Core Average"]);
        Add(readings, device, temperature, WindowsSensorKind.TemperatureCelsius, Identity);
    }

    private static void MapMemory(WindowsRawDevice device, ICollection<WindowsSensorReading> readings)
    {
        foreach (var sensor in device.Sensors)
        {
            if (sensor.Kind == WindowsRawSensorKind.Load)
            {
                Add(readings, device, sensor, WindowsSensorKind.UtilizationPercent, Identity);
            }
            else if (sensor.Kind == WindowsRawSensorKind.Data &&
                     sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.MemoryUsedBytes, FromGiB);
            }
            else if (sensor.Kind == WindowsRawSensorKind.Data &&
                     sensor.Name.Contains("Memory Available", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.MemoryAvailableBytes, FromGiB);
            }
        }
    }

    private static void MapGpu(WindowsRawDevice device, ICollection<WindowsSensorReading> readings)
    {
        var load = SelectByName(
            device.Sensors.Where(sensor => sensor.Kind == WindowsRawSensorKind.Load),
            ["GPU Core", "D3D 3D"]);
        Add(readings, device, load, WindowsSensorKind.UtilizationPercent, Identity);

        var temperature = SelectByName(
            device.Sensors.Where(sensor => sensor.Kind == WindowsRawSensorKind.Temperature),
            ["GPU Core", "GPU Hot Spot"]);
        Add(readings, device, temperature, WindowsSensorKind.TemperatureCelsius, Identity);

        foreach (var sensor in device.Sensors.Where(sensor =>
                     sensor.Kind is WindowsRawSensorKind.SmallData or WindowsRawSensorKind.Data))
        {
            Func<double, double?> converter = sensor.Kind == WindowsRawSensorKind.SmallData
                ? FromMiB
                : FromGiB;
            if (sensor.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.MemoryUsedBytes, converter);
            }
            else if (sensor.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.MemoryTotalBytes, converter);
            }
            else if (sensor.Name.Contains("Memory Free", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.MemoryAvailableBytes, converter);
            }
        }
    }

    private static void MapFans(WindowsRawDevice device, ICollection<WindowsSensorReading> readings)
    {
        foreach (var sensor in device.Sensors
                     .Where(sensor => sensor.Kind == WindowsRawSensorKind.Fan)
                     .Take(MetricNormalizer.MaximumSensorsPerFamily))
        {
            Add(readings, device, sensor, WindowsSensorKind.FanRpm, Identity);
        }
    }

    private static void MapNetwork(WindowsRawDevice device, ICollection<WindowsSensorReading> readings)
    {
        foreach (var sensor in device.Sensors.Where(sensor =>
                     sensor.Kind == WindowsRawSensorKind.Throughput))
        {
            if (sensor.Name.Contains("Download", StringComparison.OrdinalIgnoreCase) ||
                sensor.Name.Contains("Receive", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.ReceiveBytesPerSecond, Identity);
            }
            else if (sensor.Name.Contains("Upload", StringComparison.OrdinalIgnoreCase) ||
                     sensor.Name.Contains("Transmit", StringComparison.OrdinalIgnoreCase))
            {
                Add(readings, device, sensor, WindowsSensorKind.TransmitBytesPerSecond, Identity);
            }
        }
    }

    private static WindowsRawSensor? SelectByName(
        IEnumerable<WindowsRawSensor> sensors,
        IReadOnlyList<string> names)
    {
        var candidates = sensors.ToArray();
        foreach (var name in names)
        {
            var exact = candidates.FirstOrDefault(sensor =>
                sensor.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return null;
    }

    private static void Add(
        ICollection<WindowsSensorReading> readings,
        WindowsRawDevice device,
        WindowsRawSensor? sensor,
        WindowsSensorKind kind,
        Func<double, double?> convert)
    {
        if (sensor?.Value is not { } value || !double.IsFinite(value))
        {
            return;
        }

        var converted = convert(value);
        if (converted is null)
        {
            return;
        }

        readings.Add(new WindowsSensorReading(
            device.Kind,
            device.Name,
            kind,
            sensor.Name,
            converted.Value));
    }

    private static double? Identity(double value) => value;

    private static double? FromGiB(double value) => ToBytes(value, 1024d * 1024 * 1024);

    private static double? FromMiB(double value) => ToBytes(value, 1024d * 1024);

    private static double? ToBytes(double value, double multiplier)
    {
        var bytes = value * multiplier;
        return value >= 0 &&
               double.IsFinite(bytes) &&
               bytes <= MetricNormalizer.MaximumSafeJsonInteger
            ? Math.Round(bytes, MidpointRounding.AwayFromZero)
            : null;
    }
}
