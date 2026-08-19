using ReachCommander.Domain.Sources;

namespace ReachCommander.Application.Sources;

public interface ISourceCatalog
{
    ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
        CancellationToken cancellationToken);

    ValueTask<SourceDefinition> GetRequiredAsync(
        string sourceId,
        CancellationToken cancellationToken);
}
