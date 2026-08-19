using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Windows;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class LibreHardwareMonitorAdapterTests
{
    [Fact]
    public void Read_opens_once_updates_each_time_and_maps_only_approved_sensors()
    {
        var session = new FakeLibreHardwareSession([
            Device(WindowsDeviceKind.Cpu, "CPU", "/cpu/0", [
                Sensor(WindowsRawSensorKind.Load, "CPU Core #1", 40),
                Sensor(WindowsRawSensorKind.Load, "CPU Total", 25),
                Sensor(WindowsRawSensorKind.Temperature, "CPU Core #1", 70),
                Sensor(WindowsRawSensorKind.Temperature, "CPU Package", 58),
                Sensor(WindowsRawSensorKind.Power, "CPU Package", 120),
            ]),
            Device(WindowsDeviceKind.Memory, "Memory", "/memory", [
                Sensor(WindowsRawSensorKind.Data, "Memory Used", 6),
                Sensor(WindowsRawSensorKind.Data, "Memory Available", 10),
                Sensor(WindowsRawSensorKind.Load, "Memory", 37.5),
            ]),
            Device(WindowsDeviceKind.GpuNvidia, "RTX", "/pci/secret", [
                Sensor(WindowsRawSensorKind.Load, "GPU Core", 72),
                Sensor(WindowsRawSensorKind.SmallData, "GPU Memory Used", 2048),
                Sensor(WindowsRawSensorKind.SmallData, "GPU Memory Total", 8192),
                Sensor(WindowsRawSensorKind.Temperature, "GPU Core", 64),
            ]),
            Device(WindowsDeviceKind.Motherboard, "Board", "/board", [
                Sensor(WindowsRawSensorKind.Fan, "CPU Fan", 1400),
            ]),
            Device(WindowsDeviceKind.Network, "Ethernet", "/network", [
                Sensor(WindowsRawSensorKind.Throughput, "Download Speed", 1000),
                Sensor(WindowsRawSensorKind.Throughput, "Upload Speed", 500),
            ]),
        ]);
        var adapter = CreateAdapter(session, StubHostPlatform.Windows);

        var first = adapter.Read();
        var second = adapter.Read();

        Assert.Equal(1, session.OpenCalls);
        Assert.Equal(2, session.UpdateCalls);
        Assert.Contains(first, reading =>
            reading.DeviceKind == WindowsDeviceKind.Cpu &&
            reading.SensorKind == WindowsSensorKind.UtilizationPercent &&
            reading.Value == 25);
        Assert.Contains(first, reading =>
            reading.SensorKind == WindowsSensorKind.MemoryUsedBytes &&
            reading.Value == 6 * 1024d * 1024 * 1024);
        Assert.Contains(first, reading =>
            reading.SensorKind == WindowsSensorKind.MemoryTotalBytes &&
            reading.Value == 8192 * 1024d * 1024);
        Assert.Contains(first, reading => reading.SensorKind == WindowsSensorKind.FanRpm);
        Assert.Contains(first, reading => reading.SensorKind == WindowsSensorKind.ReceiveBytesPerSecond);
        Assert.DoesNotContain(first, reading => reading.SensorName.Contains("Power", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("/pci/secret", string.Join('|', second), StringComparison.Ordinal);
    }

    [Fact]
    public void Read_does_not_open_off_windows_and_dispose_is_idempotent()
    {
        var session = new FakeLibreHardwareSession([]);
        var adapter = CreateAdapter(session, StubHostPlatform.Linux);

        Assert.Empty(adapter.Read());
        adapter.Dispose();
        adapter.Dispose();

        Assert.Equal(0, session.OpenCalls);
        Assert.Equal(1, session.DisposeCalls);
    }

    private static LibreHardwareMonitorAdapter CreateAdapter(
        ILibreHardwareSession session,
        IHostPlatform platform) => new(
            session,
            platform,
            Options.Create(new HardwareMetricsOptions()),
            NullLogger<LibreHardwareMonitorAdapter>.Instance);

    private static WindowsRawDevice Device(
        WindowsDeviceKind kind,
        string name,
        string identifier,
        IReadOnlyList<WindowsRawSensor> sensors) => new(kind, name, identifier, sensors);

    private static WindowsRawSensor Sensor(
        WindowsRawSensorKind kind,
        string name,
        double value) => new(kind, name, value);

    private sealed class FakeLibreHardwareSession(
        IReadOnlyList<WindowsRawDevice> devices) : ILibreHardwareSession
    {
        public int OpenCalls { get; private set; }
        public int UpdateCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Open() => OpenCalls++;
        public void Update() => UpdateCalls++;
        public IReadOnlyList<WindowsRawDevice> ReadDevices() => devices;
        public void Dispose() => DisposeCalls++;
    }
}
