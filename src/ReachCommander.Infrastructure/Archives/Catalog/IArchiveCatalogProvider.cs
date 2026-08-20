using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.Infrastructure.Archives.Catalog;

internal sealed record ResolvedArchiveCatalog(
    ResolvedArchivePartSet PartSet,
    ArchiveCatalog Catalog);

internal interface IArchiveCatalogProvider
{
    ValueTask<ResolvedArchiveCatalog> GetAsync(
        string sourceId,
        string archivePath,
        CancellationToken cancellationToken);
}
