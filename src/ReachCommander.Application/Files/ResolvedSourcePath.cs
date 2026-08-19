using ReachCommander.Domain.Sources;

namespace ReachCommander.Application.Files;

public sealed record ResolvedSourcePath(
    SourceDefinition Source,
    string LogicalPath,
    string PhysicalPath);
