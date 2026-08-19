using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SystemMetrics;
using ReachCommander.Infrastructure.SystemMetrics.Gpu;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class NativeNvidiaNvmlApiTests
{
    [Fact]
    public void Read_falls_back_to_second_library_and_maps_devices_without_optional_temperature()
    {
        var loader = CreateSuccessfulLoader(out var shutdownCalls);
        loader.FailedLibraries.Add("libnvidia-ml.so.1");
        var api = CreateApi(loader);

        var result = api.TryReadDevices();
        api.Dispose();
        api.Dispose();

        Assert.Equal(2, result?.Count);
        Assert.Equal("GPU 1", result?[0].Name);
        Assert.Equal(25, result?[0].UtilizationPercent);
        Assert.Equal(2_000, result?[0].MemoryUsedBytes);
        Assert.Null(result?[0].TemperatureCelsius);
        Assert.Equal(["libnvidia-ml.so.1", "libnvidia-ml.so"], loader.LoadAttempts);
        Assert.Equal(1, shutdownCalls());
        Assert.Equal(1, loader.FreeCalls);
    }

    [Fact]
    public void Read_returns_unavailable_and_releases_library_when_initialization_fails()
    {
        var loader = CreateSuccessfulLoader(out var shutdownCalls);
        loader.Exports["nvmlInit_v2"] = (NvmlInitDelegate)(() => 7);
        var api = CreateApi(loader);

        var result = api.TryReadDevices();
        api.Dispose();

        Assert.Null(result);
        Assert.Equal(0, shutdownCalls());
        Assert.Equal(1, loader.FreeCalls);
    }

    [Fact]
    public void Read_does_not_load_a_native_library_off_linux()
    {
        var loader = CreateSuccessfulLoader(out _);
        var api = new NativeNvidiaNvmlApi(
            loader,
            StubHostPlatform.Windows,
            Options.Create(new HardwareMetricsOptions()),
            NullLogger<NativeNvidiaNvmlApi>.Instance);

        var result = api.TryReadDevices();

        Assert.Null(result);
        Assert.Empty(loader.LoadAttempts);
    }

    private static NativeNvidiaNvmlApi CreateApi(FakeNativeLibraryLoader loader) => new(
        loader,
        StubHostPlatform.Linux,
        Options.Create(new HardwareMetricsOptions()),
        NullLogger<NativeNvidiaNvmlApi>.Instance);

    private static FakeNativeLibraryLoader CreateSuccessfulLoader(out Func<int> shutdownCalls)
    {
        var loader = new FakeNativeLibraryLoader();
        var shutdownCount = 0;
        shutdownCalls = () => shutdownCount;
        loader.Exports["nvmlInit_v2"] = (NvmlInitDelegate)(() => 0);
        loader.Exports["nvmlShutdown"] = (NvmlShutdownDelegate)(() =>
        {
            shutdownCount++;
            return 0;
        });
        loader.Exports["nvmlDeviceGetCount_v2"] = (NvmlDeviceGetCountDelegate)((out uint count) =>
        {
            count = 2;
            return 0;
        });
        loader.Exports["nvmlDeviceGetHandleByIndex_v2"] =
            (NvmlDeviceGetHandleByIndexDelegate)((uint index, out nint device) =>
            {
                device = (nint)(index + 1);
                return 0;
            });
        loader.Exports["nvmlDeviceGetName"] =
            (NvmlDeviceGetNameDelegate)((nint device, byte[] buffer, uint length) =>
            {
                var name = Encoding.UTF8.GetBytes($"GPU {device}");
                Array.Copy(name, buffer, name.Length);
                return 0;
            });
        loader.Exports["nvmlDeviceGetUtilizationRates"] =
            (NvmlDeviceGetUtilizationRatesDelegate)((nint device, out NvmlUtilization utilization) =>
            {
                utilization = new NvmlUtilization(25, 10);
                return 0;
            });
        loader.Exports["nvmlDeviceGetMemoryInfo"] =
            (NvmlDeviceGetMemoryInfoDelegate)((nint device, out NvmlMemory memory) =>
            {
                memory = new NvmlMemory(8_000, 6_000, 2_000);
                return 0;
            });
        return loader;
    }

    private sealed class FakeNativeLibraryLoader : INativeLibraryLoader
    {
        public HashSet<string> FailedLibraries { get; } = new(StringComparer.Ordinal);
        public List<string> LoadAttempts { get; } = [];
        public Dictionary<string, Delegate> Exports { get; } = new(StringComparer.Ordinal);
        public int FreeCalls { get; private set; }

        public bool TryLoad(string libraryName, out nint handle)
        {
            LoadAttempts.Add(libraryName);
            handle = FailedLibraries.Contains(libraryName) ? 0 : (nint)123;
            return handle != 0;
        }

        public TDelegate? TryGetExport<TDelegate>(nint handle, string exportName)
            where TDelegate : Delegate =>
            Exports.TryGetValue(exportName, out var value) ? (TDelegate)value : null;

        public void Free(nint handle) => FreeCalls++;
    }
}
