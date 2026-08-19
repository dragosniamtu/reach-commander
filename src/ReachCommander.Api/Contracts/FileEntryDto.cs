using ReachCommander.Domain.Files;

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
    string Attributes)
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
        entry.Attributes);
}
