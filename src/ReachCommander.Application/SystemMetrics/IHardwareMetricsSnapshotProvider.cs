namespace ReachCommander.Application.SystemMetrics;

public interface IHardwareMetricsSnapshotProvider
{
    HardwareMetricsSnapshot GetCurrent();
}
