using ReachCommander.Domain.Files;
using ReachCommander.Domain.Archives;

namespace ReachCommander.Api.Contracts;

public sealed record FileEntryDto(
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
    ArchiveRole? ArchiveRole)
{
    public static FileEntryDto FromEntry(FileEntry entry) => new(
        entry.Name,
        entry.RelativePath,
        entry.Type,
        entry.Size,
        entry.ModifiedAt,
        entry.Extension,
        entry.IsReadOnly,
        entry.IsSymbolicLink,
        entry.Attributes,
        entry.ArchiveFormatHint,
        entry.ArchiveRole);
}
