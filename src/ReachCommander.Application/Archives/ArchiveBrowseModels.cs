using ReachCommander.Domain.Archives;

namespace ReachCommander.Application.Archives;

public sealed record ArchiveLocation(
    string SourceId,
    string ArchivePath,
    string InternalPath);

public sealed record ArchiveDirectoryListing(
    ArchiveLocation Location,
    ArchiveFormat Format,
    int VolumeCount,
    IReadOnlyList<ArchiveEntry> Entries);
