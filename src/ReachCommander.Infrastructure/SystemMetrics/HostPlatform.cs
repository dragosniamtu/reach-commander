namespace ReachCommander.Infrastructure.SystemMetrics;

internal interface IHostPlatform
{
    bool IsLinux { get; }
    bool IsWindows { get; }
}

internal sealed class RuntimeHostPlatform : IHostPlatform
{
    public bool IsLinux => OperatingSystem.IsLinux();
    public bool IsWindows => OperatingSystem.IsWindows();
}
