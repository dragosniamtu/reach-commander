using ReachCommander.Domain.Sources;

namespace ReachCommander.Api.Contracts;

public sealed record SourceDto(
    string Id,
    string Name,
    bool IsAvailable,
    bool IsReadOnly,
    long? TotalBytes,
    long? UsedBytes,
    long? FreeBytes,
    bool DefaultLeft,
    bool DefaultRight)
{
    public static SourceDto FromSnapshot(SourceSnapshot source) => new(
        source.Id,
        source.Name,
        source.IsAvailable,
        source.IsReadOnly,
        source.TotalBytes,
        source.UsedBytes,
        source.FreeBytes,
        source.DefaultLeft,
        source.DefaultRight);
}
