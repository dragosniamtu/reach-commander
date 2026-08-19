namespace ReachCommander.Infrastructure.Configuration;

internal sealed class SourcesFile
{
    public List<SourceFileEntry> Sources { get; init; } = [];
}

internal sealed class SourceFileEntry
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public bool ReadOnly { get; init; }

    public bool DefaultLeft { get; init; }

    public bool DefaultRight { get; init; }
}
