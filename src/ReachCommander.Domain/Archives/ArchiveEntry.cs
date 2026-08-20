namespace ReachCommander.Domain.Archives;

public sealed record ArchiveEntry(
    string Path,
    string Name,
    ArchiveEntryType Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Extension,
    string Attributes);
