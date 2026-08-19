namespace ReachCommander.Infrastructure.SystemMetrics.Gpu;

internal enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
}

internal static class GpuVendorIds
{
    public static GpuVendor? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "0x10de" => GpuVendor.Nvidia,
        "0x1002" => GpuVendor.Amd,
        "0x8086" => GpuVendor.Intel,
        _ => null,
    };

    public static string DisplayName(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => throw new ArgumentOutOfRangeException(nameof(vendor)),
    };
}
