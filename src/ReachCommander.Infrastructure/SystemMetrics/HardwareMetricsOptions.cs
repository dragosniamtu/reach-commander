namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class HardwareMetricsOptions
{
    public const string SectionName = "HardwareMetrics";

    public bool Enabled { get; init; } = true;
    public int SampleIntervalSeconds { get; init; } = 5;
    public int StaleAfterSeconds { get; init; } = 15;
    public int CollectorTimeoutMilliseconds { get; init; } = 2000;
    public string LinuxProcRoot { get; init; } = "/proc";
    public string LinuxSysRoot { get; init; } = "/sys";
    public bool TemperaturesEnabled { get; init; } = true;
    public bool FansEnabled { get; init; } = true;
    public bool NetworkEnabled { get; init; } = true;
    public bool GpusEnabled { get; init; } = true;
}
