namespace ReachCommander.Domain.Sources;

public sealed record SourceDefinition(
    string Id,
    string Name,
    string RootPath,
    bool IsReadOnly,
    bool DefaultLeft,
    bool DefaultRight);
