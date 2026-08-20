namespace ReachCommander.Application.Archives;

public interface IArchiveBrowser
{
    ValueTask<ArchiveDirectoryListing> ListAsync(
        ArchiveLocation location,
        CancellationToken cancellationToken);
}
