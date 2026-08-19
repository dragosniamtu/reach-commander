using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Gpu;
using ReachCommander.Infrastructure.SystemMetrics.Linux;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class LinuxGpuCollectorTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();

    [Theory]
    [InlineData("0x1002", "AMD", "42", 42)]
    [InlineData("0x8086", "Intel", "17", 17)]
    public async Task Drm_collector_detects_vendor_and_maps_available_fields(
        string vendorId,
        string vendorName,
        string busy,
        double expectedBusy)
    {
        Write("class/drm/card0/device/vendor", vendorId);
        Write("class/drm/card0/device/gpu_busy_percent", busy);
        Write("class/drm/card0/device/mem_info_vram_used", "1048576");
        Write("class/drm/card0/device/mem_info_vram_total", "4194304");
        Write("class/drm/card0/device/hwmon/hwmon0/temp1_input", "61000");
        Write("class/drm/card0/device/hwmon/hwmon0/temp1_max", "90000");
        Write("class/drm/card0/device/hwmon/hwmon0/temp1_crit", "100000");
        var collector = CreateDrmCollector();

        var result = await collector.CollectAsync(CancellationToken.None);

        var gpu = Assert.Single(result.Gpus!);
        Assert.Equal(HardwareCollectorState.Success, result.Status.State);
        Assert.Equal(vendorName, gpu.Vendor);
        Assert.Equal(expectedBusy, gpu.UtilizationPercent);
        Assert.Equal(1_048_576, gpu.MemoryUsedBytes);
        Assert.Equal(4_194_304, gpu.MemoryTotalBytes);
        Assert.Equal(61, gpu.TemperatureCelsius);
        Assert.Equal(90, gpu.WarningTemperatureCelsius);
        Assert.Equal(100, gpu.CriticalTemperatureCelsius);
        Assert.DoesNotContain("card0", gpu.Id, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Drm_collector_nulls_used_memory_when_it_exceeds_total()
    {
        Write("class/drm/card0/device/vendor", "0x1002");
        Write("class/drm/card0/device/mem_info_vram_used", "20");
        Write("class/drm/card0/device/mem_info_vram_total", "10");

        var result = await CreateDrmCollector().CollectAsync(CancellationToken.None);

        var gpu = Assert.Single(result.Gpus!);
        Assert.Null(gpu.MemoryUsedBytes);
        Assert.Equal(10, gpu.MemoryTotalBytes);
    }

    [Fact]
    public async Task Nvidia_collector_maps_safe_samples_and_isolates_unavailable_library()
    {
        INvidiaNvmlApi api = new StubNvmlApi([
            new NvidiaDeviceSample("GeForce RTX\0 Test", 72, 2_000, 8_000, 64),
        ]);
        var collector = new NvidiaNvmlCollector(
            Options.Create(new HardwareMetricsOptions()),
            api,
            StubHostPlatform.Linux);

        var result = await collector.CollectAsync(CancellationToken.None);

        var gpu = Assert.Single(result.Gpus!);
        Assert.Equal("NVIDIA", gpu.Vendor);
        Assert.Equal("GeForce RTX Test", gpu.Name);
        Assert.Equal(72, gpu.UtilizationPercent);
        Assert.Equal(64, gpu.TemperatureCelsius);

        var unavailable = await new NvidiaNvmlCollector(
                Options.Create(new HardwareMetricsOptions()),
                new StubNvmlApi(null),
                StubHostPlatform.Linux)
            .CollectAsync(CancellationToken.None);
        Assert.Equal(HardwareCollectorState.Unsupported, unavailable.Status.State);
    }

    private LinuxDrmGpuCollector CreateDrmCollector() => new(
        Options.Create(new HardwareMetricsOptions { LinuxSysRoot = _temporary.Path }),
        new BoundedTextFileReader(),
        new TrustedPathResolver(StubHostPlatform.Linux),
        StubHostPlatform.Linux);

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_temporary.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose() => _temporary.Dispose();

    private sealed class StubNvmlApi(IReadOnlyList<NvidiaDeviceSample>? samples) : INvidiaNvmlApi
    {
        public IReadOnlyList<NvidiaDeviceSample>? TryReadDevices() => samples;
        public void Dispose()
        {
        }
    }
}
