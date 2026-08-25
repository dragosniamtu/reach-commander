namespace ReachCommander.Infrastructure.SystemUpdates;

internal sealed class SystemUpdateOptions
{
    public const string SectionName = "SystemUpdates";

    public bool Enabled { get; init; } = true;

    public string SocketPath { get; init; } = "/run/reachcommander-updater/updater.sock";

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(15);
}
