using ReachCommander.Application.Directories;

namespace ReachCommander.Api.Contracts.Directories;

public sealed record CreateDirectoryRequestDto(
    string SourceId,
    string ParentLogicalPath,
    string Name)
{
    internal CreateDirectoryRequest ToModel() => new(SourceId, ParentLogicalPath, Name);
}
