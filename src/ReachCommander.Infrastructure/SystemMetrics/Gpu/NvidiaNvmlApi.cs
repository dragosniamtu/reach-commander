using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.SystemMetrics.Gpu;

internal sealed record NvidiaDeviceSample(
    string Name,
    double? UtilizationPercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    double? TemperatureCelsius);

internal interface INvidiaNvmlApi : IDisposable
{
    IReadOnlyList<NvidiaDeviceSample>? TryReadDevices();
}

internal interface INativeLibraryLoader
{
    bool TryLoad(string libraryName, out nint handle);
    TDelegate? TryGetExport<TDelegate>(nint handle, string exportName)
        where TDelegate : Delegate;
    void Free(nint handle);
}

internal sealed class RuntimeNativeLibraryLoader : INativeLibraryLoader
{
    public bool TryLoad(string libraryName, out nint handle) =>
        NativeLibrary.TryLoad(libraryName, out handle);

    public TDelegate? TryGetExport<TDelegate>(nint handle, string exportName)
        where TDelegate : Delegate =>
        NativeLibrary.TryGetExport(handle, exportName, out var address)
            ? Marshal.GetDelegateForFunctionPointer<TDelegate>(address)
            : null;

    public void Free(nint handle) => NativeLibrary.Free(handle);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlInitDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlShutdownDelegate();

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlDeviceGetCountDelegate(out uint count);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlDeviceGetHandleByIndexDelegate(uint index, out nint device);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlDeviceGetNameDelegate(
    nint device,
    [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] byte[] name,
    uint length);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlDeviceGetUtilizationRatesDelegate(
    nint device,
    out NvmlUtilization utilization);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlDeviceGetMemoryInfoDelegate(
    nint device,
    out NvmlMemory memory);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int NvmlDeviceGetTemperatureDelegate(
    nint device,
    uint sensorType,
    out uint temperature);

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization(uint gpu, uint memory)
{
    public uint Gpu = gpu;
    public uint Memory = memory;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory(ulong total, ulong free, ulong used)
{
    public ulong Total = total;
    public ulong Free = free;
    public ulong Used = used;
}

internal sealed class NativeNvidiaNvmlApi(
    INativeLibraryLoader libraryLoader,
    IHostPlatform platform,
    IOptions<HardwareMetricsOptions> options,
    ILogger<NativeNvidiaNvmlApi> logger) : INvidiaNvmlApi
{
    private static readonly string[] LibraryNames = ["libnvidia-ml.so.1", "libnvidia-ml.so"];
    private readonly object _gate = new();
    private nint _libraryHandle;
    private bool _initialized;
    private bool _disposed;
    private NvmlShutdownDelegate? _shutdown;
    private NvmlDeviceGetCountDelegate? _getCount;
    private NvmlDeviceGetHandleByIndexDelegate? _getHandleByIndex;
    private NvmlDeviceGetNameDelegate? _getName;
    private NvmlDeviceGetUtilizationRatesDelegate? _getUtilization;
    private NvmlDeviceGetMemoryInfoDelegate? _getMemory;
    private NvmlDeviceGetTemperatureDelegate? _getTemperature;

    public IReadOnlyList<NvidiaDeviceSample>? TryReadDevices()
    {
        if (!platform.IsLinux || Volatile.Read(ref _disposed))
        {
            return null;
        }

        lock (_gate)
        {
            if (_disposed || !EnsureInitialized())
            {
                return null;
            }

            try
            {
                if (_getCount!(out var count) != 0)
                {
                    return null;
                }

                var boundedCount = Math.Min(count, (uint)MetricNormalizer.MaximumGpuCount);
                var samples = new List<NvidiaDeviceSample>((int)boundedCount);
                for (uint index = 0; index < boundedCount; index++)
                {
                    if (_getHandleByIndex!(index, out var device) != 0)
                    {
                        continue;
                    }

                    var nameBuffer = new byte[MetricNormalizer.MaximumLabelLength];
                    if (_getName!(device, nameBuffer, (uint)nameBuffer.Length) != 0)
                    {
                        continue;
                    }

                    double? utilization = null;
                    if (_getUtilization is not null &&
                        _getUtilization(device, out var utilizationSample) == 0)
                    {
                        utilization = utilizationSample.Gpu;
                    }

                    long? used = null;
                    long? total = null;
                    if (_getMemory is not null && _getMemory(device, out var memory) == 0)
                    {
                        used = ToSafeInteger(memory.Used);
                        total = ToSafeInteger(memory.Total);
                    }

                    double? temperature = null;
                    if (_getTemperature is not null &&
                        _getTemperature(device, 0, out var temperatureSample) == 0)
                    {
                        temperature = temperatureSample;
                    }

                    samples.Add(new NvidiaDeviceSample(
                        DecodeName(nameBuffer),
                        utilization,
                        used,
                        total,
                        temperature));
                }

                return samples.AsReadOnly();
            }
            catch (Exception exception) when (IsExpectedNativeFailure(exception))
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true))
        {
            return;
        }

        if (!Monitor.TryEnter(_gate, options.Value.CollectorTimeoutMilliseconds))
        {
            logger.LogWarning("NVIDIA native library disposal deferred because collection is still active.");
            return;
        }

        try
        {
            ReleaseLibrary(callShutdown: _initialized);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private bool EnsureInitialized()
    {
        if (_initialized)
        {
            return true;
        }

        try
        {
            foreach (var libraryName in LibraryNames)
            {
                if (libraryLoader.TryLoad(libraryName, out _libraryHandle))
                {
                    break;
                }
            }

            if (_libraryHandle == 0)
            {
                return false;
            }

            var initialize = libraryLoader.TryGetExport<NvmlInitDelegate>(_libraryHandle, "nvmlInit_v2");
            _shutdown = libraryLoader.TryGetExport<NvmlShutdownDelegate>(_libraryHandle, "nvmlShutdown");
            _getCount = libraryLoader.TryGetExport<NvmlDeviceGetCountDelegate>(
                _libraryHandle,
                "nvmlDeviceGetCount_v2");
            _getHandleByIndex = libraryLoader.TryGetExport<NvmlDeviceGetHandleByIndexDelegate>(
                _libraryHandle,
                "nvmlDeviceGetHandleByIndex_v2");
            _getName = libraryLoader.TryGetExport<NvmlDeviceGetNameDelegate>(
                _libraryHandle,
                "nvmlDeviceGetName");

            if (initialize is null ||
                _shutdown is null ||
                _getCount is null ||
                _getHandleByIndex is null ||
                _getName is null ||
                initialize() != 0)
            {
                ReleaseLibrary(callShutdown: false);
                return false;
            }

            _getUtilization = libraryLoader.TryGetExport<NvmlDeviceGetUtilizationRatesDelegate>(
                _libraryHandle,
                "nvmlDeviceGetUtilizationRates");
            _getMemory = libraryLoader.TryGetExport<NvmlDeviceGetMemoryInfoDelegate>(
                _libraryHandle,
                "nvmlDeviceGetMemoryInfo");
            _getTemperature = libraryLoader.TryGetExport<NvmlDeviceGetTemperatureDelegate>(
                _libraryHandle,
                "nvmlDeviceGetTemperature");
            _initialized = true;
            return true;
        }
        catch (Exception exception) when (IsExpectedNativeFailure(exception))
        {
            ReleaseLibrary(callShutdown: false);
            return false;
        }
    }

    private void ReleaseLibrary(bool callShutdown)
    {
        if (callShutdown)
        {
            _shutdown?.Invoke();
        }

        if (_libraryHandle != 0)
        {
            libraryLoader.Free(_libraryHandle);
        }

        _libraryHandle = 0;
        _initialized = false;
        _shutdown = null;
        _getCount = null;
        _getHandleByIndex = null;
        _getName = null;
        _getUtilization = null;
        _getMemory = null;
        _getTemperature = null;
    }

    private static string DecodeName(byte[] buffer)
    {
        var terminator = Array.IndexOf(buffer, (byte)0);
        var length = terminator < 0 ? buffer.Length : terminator;
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static long? ToSafeInteger(ulong value) =>
        value <= (ulong)MetricNormalizer.MaximumSafeJsonInteger ? (long)value : null;

    private static bool IsExpectedNativeFailure(Exception exception) =>
        exception is ArgumentException or
            BadImageFormatException or
            DllNotFoundException or
            EntryPointNotFoundException or
            InvalidOperationException or
            MarshalDirectiveException or
            OverflowException or
            SEHException;
}
