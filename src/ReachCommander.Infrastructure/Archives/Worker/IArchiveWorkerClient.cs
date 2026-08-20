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

    ValueTask ExtractAsync(
        ResolvedArchivePartSet partSet,
        IReadOnlyList<int> entryIndexes,
        IArchiveEntrySink sink,
        CancellationToken cancellationToken);
}

internal interface IArchiveEntrySink
{
    ValueTask StartAsync(int entryIndex, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    ValueTask EndAsync(int entryIndex, long actualBytes, CancellationToken cancellationToken);

    ValueTask ProgressAsync(int completedFiles, long actualBytes, CancellationToken cancellationToken);
}
