using ReachCommander.Domain.Archives;

namespace ReachCommander.Domain.Files;

public sealed record FileEntry(
    string Name,
    string RelativePath,
    FileEntryType Type,
    long? Size,
    DateTimeOffset ModifiedAt,
    string? Extension,
    bool IsReadOnly,
    bool IsSymbolicLink,
    string Attributes,
    ArchiveFormat? ArchiveFormatHint,
    ArchiveRole? ArchiveRole);
