namespace ReachCommander.Application.SystemMetrics;

public sealed class HardwareMetricsNotReadyException()
    : Exception("Hardware metrics have not completed their first sample.")
{
}
