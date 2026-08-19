using ReachCommander.Domain.Files;

namespace ReachCommander.Application.Files;

public interface IFileBrowser
{
    ValueTask<IReadOnlyList<FileEntry>> ListAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken);

    ValueTask<FileEntry> GetInfoAsync(
        string sourceId,
        string logicalPath,
        CancellationToken cancellationToken);
}
