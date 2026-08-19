using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class HardwareMetricsSnapshotCache(
    IOptions<HardwareMetricsOptions> options,
    TimeProvider timeProvider) : IHardwareMetricsSnapshotProvider
{
    private HardwareMetricsSnapshot? _snapshot;

    public void Set(HardwareMetricsSnapshot snapshot) =>
        Interlocked.Exchange(ref _snapshot, snapshot);

    public HardwareMetricsSnapshot GetCurrent()
    {
        var snapshot = Volatile.Read(ref _snapshot)
            ?? throw new HardwareMetricsNotReadyException();

        if (snapshot.State == HardwareMetricsState.Disabled)
        {
            return snapshot;
        }

        return timeProvider.GetUtcNow() - snapshot.SampledAt >
               TimeSpan.FromSeconds(options.Value.StaleAfterSeconds)
            ? snapshot with { State = HardwareMetricsState.Stale }
            : snapshot;
    }
}
