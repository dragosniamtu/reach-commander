using ReachCommander.Domain.Archives;
using ReachCommander.Infrastructure.Archives.Catalog;
using ReachCommander.Infrastructure.Archives.Volumes;

namespace ReachCommander.Infrastructure.Archives.Worker;

internal sealed record ArchiveWorkerInspection(
    ArchiveFormat Format,
    bool IsSolid,
    IReadOnlyList<UntrustedArchiveEntry> Entries);

internal interface IArchiveWorkerClient
{
    ValueTask<ArchiveWorkerInspection> InspectAsync(
        ResolvedArchivePartSet partSet,
        CancellationToken cancellationToken);
}
