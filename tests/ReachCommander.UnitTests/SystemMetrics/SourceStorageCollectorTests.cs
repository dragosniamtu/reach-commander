using ReachCommander.Application.Sources;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.SystemMetrics;

namespace ReachCommander.UnitTests.SystemMetrics;

public sealed class SourceStorageCollectorTests
{
    [Fact]
    public async Task Collect_maps_safe_source_capacity_and_unavailable_sources()
    {
        ISourceCatalog catalog = new StubSourceCatalog([
            new SourceSnapshot("media", "Media", true, false, 1000, 750, 250, true, true),
            new SourceSnapshot("usb", "USB", false, true, null, null, null, false, false),
        ]);

        var result = await new SourceStorageCollector(catalog)
            .CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Success, result.Status.State);
        Assert.Equal(75, result.Storage![0].UtilizationPercent);
        Assert.False(result.Storage[1].IsAvailable);
        Assert.Equal(["media", "usb"], result.Storage.Select(item => item.SourceId));
        Assert.DoesNotContain("path", string.Join('|', result.Storage), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Collect_returns_a_safe_failure_without_catalog_exception_text()
    {
        const string secret = "D:\\private\\media";
        ISourceCatalog catalog = new ThrowingSourceCatalog(new IOException(secret));

        var result = await new SourceStorageCollector(catalog)
            .CollectAsync(CancellationToken.None);

        Assert.Equal(HardwareCollectorState.Failed, result.Status.State);
        Assert.Equal("source_storage_unavailable", result.Status.Code);
        Assert.DoesNotContain(secret, result.Status.Code, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubSourceCatalog(IReadOnlyList<SourceSnapshot> snapshots) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(snapshots);

        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ThrowingSourceCatalog(Exception exception) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => ValueTask.FromException<IReadOnlyList<SourceSnapshot>>(exception);

        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
