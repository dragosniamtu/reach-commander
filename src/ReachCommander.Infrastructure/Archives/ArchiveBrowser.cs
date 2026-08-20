using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;

namespace ReachCommander.Infrastructure.Archives;

internal sealed class ArchiveBrowser(IArchiveCatalogProvider catalogProvider) : IArchiveBrowser
{
    public async ValueTask<ArchiveDirectoryListing> ListAsync(
        ArchiveLocation location,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (string.IsNullOrWhiteSpace(location.SourceId) ||
            string.IsNullOrWhiteSpace(location.ArchivePath) ||
            string.IsNullOrWhiteSpace(location.InternalPath))
        {
            throw new ArchiveInvalidException();
        }

        var resolved = await catalogProvider.GetAsync(
            location.SourceId,
            location.ArchivePath,
            cancellationToken);
        var entries = resolved.Catalog.ListChildren(location.InternalPath)
            .Select(node => new ArchiveEntry(
                node.Path,
                node.Name,
                node.Type,
                node.Size,
                node.ModifiedAt,
                node.Type == ArchiveEntryType.File ? node.Extension : null,
                "Archive · RO"))
            .ToArray();
        return new ArchiveDirectoryListing(
            location,
            resolved.Catalog.Format,
            resolved.PartSet.Parts.Count,
            Array.AsReadOnly(entries));
    }
}

internal sealed class DisabledArchiveBrowser : IArchiveBrowser
{
    public ValueTask<ArchiveDirectoryListing> ListAsync(
        ArchiveLocation location,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<ArchiveDirectoryListing>(
            new ArchiveUnsupportedException());
}
