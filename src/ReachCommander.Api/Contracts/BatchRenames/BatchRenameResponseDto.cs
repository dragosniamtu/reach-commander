using ReachCommander.Application.BatchRenames;
using ReachCommander.Domain.Files;

namespace ReachCommander.Api.Contracts.BatchRenames;

public sealed record BatchRenamePreviewRowDto(
    string SourcePath,
    string OldName,
    string? OldExtension,
    string NewName,
    FileEntryType Type,
    long? Size,
    DateTimeOffset ModifiedAt,
    BatchRenamePreviewStatus Status,
    string? Message)
{
    public static BatchRenamePreviewRowDto FromModel(BatchRenamePreviewRow row) => new(
        row.SourcePath,
        row.OldName,
        row.OldExtension,
        row.NewName,
        row.Type,
        row.Size,
        row.ModifiedAt,
        row.Status,
        row.Message);
}

public sealed record BatchRenamePreviewDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<BatchRenamePreviewRowDto> Rows,
    bool CanExecute,
    int ChangedCount,
    int UnchangedCount,
    int InvalidCount)
{
    public static BatchRenamePreviewDto FromModel(BatchRenamePreview preview) => new(
        preview.PlanId,
        preview.ExpiresAt,
        preview.Rows.Select(BatchRenamePreviewRowDto.FromModel).ToArray(),
        preview.CanExecute,
        preview.ChangedCount,
        preview.UnchangedCount,
        preview.InvalidCount);
}

public sealed record BatchRenameOperationRowDto(
    string OldPath,
    string NewPath,
    string CurrentPath,
    string OldName,
    string NewName,
    string CurrentName,
    FileEntryType Type,
    BatchRenameRowResult Result,
    string? Message)
{
    public static BatchRenameOperationRowDto FromModel(BatchRenameOperationRow row) => new(
        row.OldPath,
        row.NewPath,
        row.CurrentPath,
        row.OldName,
        row.NewName,
        row.CurrentName,
        row.Type,
        row.Result,
        row.Message);
}

public sealed record BatchRenameOperationDto(
    Guid OperationId,
    BatchRenameOperationStatus Status,
    IReadOnlyList<BatchRenameOperationRowDto> Rows,
    bool CompensationAttempted,
    bool RecoveryRequired,
    bool UndoAvailable,
    DateTimeOffset? UndoExpiresAt)
{
    public static BatchRenameOperationDto FromModel(BatchRenameOperationResult result) => new(
        result.OperationId,
        result.Status,
        result.Rows.Select(BatchRenameOperationRowDto.FromModel).ToArray(),
        result.CompensationAttempted,
        result.RecoveryRequired,
        result.UndoAvailable,
        result.UndoExpiresAt);
}
