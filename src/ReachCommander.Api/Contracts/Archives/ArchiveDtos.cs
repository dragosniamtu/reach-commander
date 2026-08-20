using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;

namespace ReachCommander.Api.Contracts.Archives;

public sealed record ArchiveEntryDto(
    string Path,
    string Name,
    string Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Extension,
    string Attributes)
{
    public static ArchiveEntryDto FromEntry(ArchiveEntry entry) => new(
        entry.Path,
        entry.Name,
        entry.Type == ArchiveEntryType.Directory ? "directory" : "file",
        entry.Size,
        entry.ModifiedAt,
        entry.Extension,
        entry.Attributes);
}

public sealed record ArchiveDirectoryDto(
    string SourceId,
    string ArchivePath,
    string Path,
    string Format,
    int VolumeCount,
    bool IsReadOnly,
    IReadOnlyList<ArchiveEntryDto> Entries)
{
    public static ArchiveDirectoryDto FromListing(ArchiveDirectoryListing listing) => new(
        listing.Location.SourceId,
        listing.Location.ArchivePath,
        listing.Location.InternalPath,
        listing.Format switch
        {
            ArchiveFormat.Zip => "zip",
            ArchiveFormat.Rar => "rar",
            ArchiveFormat.SevenZip => "sevenZip",
            _ => throw new ArgumentOutOfRangeException(nameof(listing)),
        },
        listing.VolumeCount,
        true,
        Array.AsReadOnly(listing.Entries.Select(ArchiveEntryDto.FromEntry).ToArray()));
}
