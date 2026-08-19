using ReachCommander.Application.Sources;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Infrastructure.SystemMetrics;

internal sealed class SourceStorageCollector(ISourceCatalog sourceCatalog) : IHardwareMetricsCollector
{
    public string Name => "source-storage";
    public bool IsSupported => true;

    public async ValueTask<HardwareMetricsContribution> CollectAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshots = await sourceCatalog.GetSnapshotsAsync(cancellationToken);
            var storage = snapshots
                .Take(MetricNormalizer.MaximumSensorsPerFamily)
                .Select(MapStorage)
                .ToArray();

            return new HardwareMetricsContribution(
                new HardwareCollectorStatus(Name, HardwareCollectorState.Success, null),
                Storage: Array.AsReadOnly(storage));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceConfigurationException)
        {
            return Failed();
        }
        catch (UnauthorizedAccessException)
        {
            return Failed();
        }
        catch (IOException)
        {
            return Failed();
        }
    }

    private static StorageMetrics MapStorage(Domain.Sources.SourceSnapshot source)
    {
        var safeId = MetricNormalizer.Label(source.Id, "source");
        var total = MetricNormalizer.NonNegative(source.TotalBytes);
        var used = MetricNormalizer.NonNegative(source.UsedBytes);
        var free = MetricNormalizer.NonNegative(source.FreeBytes);
        var percent = total is > 0 && used is >= 0 && used <= total
            ? MetricNormalizer.Percent(100d * used.Value / total.Value)
            : null;

        return new StorageMetrics(
            safeId,
            MetricNormalizer.Label(source.Name, safeId),
            source.IsAvailable,
            used,
            free,
            total,
            percent);
    }

    private HardwareMetricsContribution Failed() => new(
        new HardwareCollectorStatus(
            Name,
            HardwareCollectorState.Failed,
            "source_storage_unavailable"));
}
