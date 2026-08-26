using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Api.Contracts.BatchRenames;

public sealed record ExactRenamePreviewRequestDto(
    string SourceId,
    string DirectoryPath,
    string EntryPath,
    string NewName)
{
    public ExactRenamePreviewCommand ToCommand() => new(
        SourceId,
        DirectoryPath,
        EntryPath,
        NewName);
}
