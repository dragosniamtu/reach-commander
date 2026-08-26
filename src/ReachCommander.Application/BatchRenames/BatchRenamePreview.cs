using ReachCommander.Domain.Files;

namespace ReachCommander.Application.BatchRenames;

public enum BatchRenamePreviewStatus
{
    Ready,
    Unchanged,
    Invalid,
    Conflict,
    Stale,
}

public sealed record BatchRenamePreviewCommand(
    string SourceId,
    string DirectoryPath,
    IReadOnlyList<string> EntryPaths,
    BatchRenameRules Rules);

public sealed record ExactRenamePreviewCommand(
    string SourceId,
    string DirectoryPath,
    string EntryPath,
    string NewName);

public sealed record BatchRenamePreviewRow(
    string SourcePath,
    string OldName,
    string? OldExtension,
    string NewName,
    FileEntryType Type,
    long? Size,
    DateTimeOffset ModifiedAt,
    BatchRenamePreviewStatus Status,
    string? Message);

public sealed record BatchRenamePreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<BatchRenamePreviewRow> Rows,
    bool CanExecute,
    int ChangedCount,
    int UnchangedCount,
    int InvalidCount);
