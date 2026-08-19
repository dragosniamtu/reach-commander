namespace ReachCommander.Domain.Sources;

public sealed record SourceSnapshot(
    string Id,
    string Name,
    bool IsAvailable,
    bool IsReadOnly,
    long? TotalBytes,
    long? UsedBytes,
    long? FreeBytes,
    bool DefaultLeft,
    bool DefaultRight);
