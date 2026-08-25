namespace ReachCommander.Application.Directories;

public sealed record CreateDirectoryRequest(
    string SourceId,
    string ParentLogicalPath,
    string Name);
