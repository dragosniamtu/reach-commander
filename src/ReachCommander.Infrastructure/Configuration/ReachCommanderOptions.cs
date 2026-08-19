namespace ReachCommander.Infrastructure.Configuration;

public sealed class ReachCommanderOptions
{
    public const string SectionName = "ReachCommander";

    public string SourcesPath { get; init; } = "/config/sources.json";
}
