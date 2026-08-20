using ReachCommander.Domain.Files;

namespace ReachCommander.Application.BatchRenames;

public enum BatchRenameOperationStatus
{
    Completed,
    Failed,
    RecoveryRequired,
    Undone,
}

public enum BatchRenameRowResult
{
    Completed,
    Unchanged,
    Failed,
    RolledBack,
    RecoveryRequired,
}

public sealed record BatchRenameOperationRow(
    string OldPath,
    string NewPath,
    string CurrentPath,
    string OldName,
    string NewName,
    string CurrentName,
    FileEntryType Type,
    BatchRenameRowResult Result,
    string? Message);

public sealed record BatchRenameOperationResult(
    Guid OperationId,
    BatchRenameOperationStatus Status,
    IReadOnlyList<BatchRenameOperationRow> Rows,
    bool CompensationAttempted,
    bool RecoveryRequired,
    bool UndoAvailable,
    DateTimeOffset? UndoExpiresAt);
