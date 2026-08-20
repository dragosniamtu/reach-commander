using ReachCommander.Application.Archives;
using ReachCommander.Infrastructure.Archives.Volumes;
using ReachCommander.Infrastructure.Archives.Worker;

namespace ReachCommander.Infrastructure.Archives.Catalog;

internal sealed class ArchiveCatalogProvider(
    IArchivePartResolver partResolver,
    IArchiveWorkerClient workerClient,
    ArchiveCatalogBuilder catalogBuilder,
    ArchiveCatalogCache cache) : IArchiveCatalogProvider
{
    public async ValueTask<ResolvedArchiveCatalog> GetAsync(
        string sourceId,
        string archivePath,
        CancellationToken cancellationToken)
    {
        var partSet = await partResolver.ResolveAsync(
            sourceId,
            archivePath,
            cancellationToken);
        var cached = cache.Get(partSet.Format, partSet.Fingerprint);
        if (cached is not null)
        {
            return new ResolvedArchiveCatalog(partSet, cached);
        }

        var inspection = await workerClient.InspectAsync(partSet, cancellationToken);
        if (inspection.Format != partSet.Format)
        {
            throw new ArchiveInvalidException();
        }

        var catalog = catalogBuilder.Build(inspection.Format, inspection.Entries);
        cache.Set(partSet.Format, partSet.Fingerprint, catalog);
        return new ResolvedArchiveCatalog(partSet, catalog);
    }
}
