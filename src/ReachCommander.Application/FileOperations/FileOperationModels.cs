using ReachCommander.Domain.Files;

namespace ReachCommander.Application.FileOperations;

public enum FileOperationKind
{
    Copy,
    Move,
    PermanentDelete,
    Trash,
    Restore,
    EmptyTrash,
}

public enum FileOperationConflictDecision
{
    Overwrite,
    Skip,
    CreateUniqueName,
}

public enum FileOperationPhase
{
    Queued,
    Validating,
    Running,
    Cancelling,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
    Interrupted,
}

public enum FileOperationItemResult
{
    Completed,
    Skipped,
    Failed,
    CopiedButNotRemoved,
    NotStarted,
}

public sealed record FileOperationPreviewRequest(
    FileOperationKind Kind,
    string SourceId,
    IReadOnlyList<string> LogicalPaths,
    string DestinationSourceId,
    string DestinationLogicalDirectory);

public sealed record FileOperationConflict(
    Guid ConflictId,
    string SourceLogicalPath,
    string DestinationLogicalPath,
    FileEntryType SourceType,
    FileEntryType DestinationType,
    IReadOnlyList<FileOperationConflictDecision> AllowedDecisions);

public sealed record FileOperationPreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    FileOperationKind Kind,
    string SourceId,
    IReadOnlyList<string> LogicalPaths,
    string DestinationSourceId,
    string DestinationLogicalDirectory,
    int TotalItems,
    long? TotalBytes,
    IReadOnlyList<FileOperationConflict> Conflicts,
    IReadOnlyList<string> Warnings);

public sealed record FileOperationConflictResolution(
    Guid ConflictId,
    FileOperationConflictDecision Decision);

public sealed record FileOperationSubmission(
    Guid PlanId,
    IReadOnlyList<FileOperationConflictResolution> Resolutions);

public sealed record FileOperationItemOutcome(
    string SourceId,
    string SourceLogicalPath,
    string? DestinationSourceId,
    string? DestinationLogicalPath,
    FileOperationItemResult Result,
    string? ErrorCode,
    string? Detail);

public sealed record FileOperationProgress(
    string? CurrentLogicalName,
    int CompletedItems,
    int TotalItems,
    long CompletedBytes,
    long? TotalBytes,
    double? Percentage,
    long? BytesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining);

public sealed record FileOperationStatus(
    Guid OperationId,
    FileOperationKind Kind,
    FileOperationPhase Phase,
    int QueuePosition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    FileOperationProgress Progress,
    IReadOnlyList<FileOperationItemOutcome> Outcomes,
    IReadOnlyList<string> Warnings,
    bool Acknowledged);
