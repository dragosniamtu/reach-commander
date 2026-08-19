using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.Support;

internal sealed class StubHostPlatform(bool isLinux, bool isWindows) : IHostPlatform
{
    public static StubHostPlatform Linux { get; } = new(true, false);
    public static StubHostPlatform Windows { get; } = new(false, true);
    public static StubHostPlatform Other { get; } = new(false, false);

    public bool IsLinux { get; } = isLinux;
    public bool IsWindows { get; } = isWindows;
}
