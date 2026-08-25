using ReachCommander.Application.FileOperations;
using ReachCommander.Domain.Files;

namespace ReachCommander.Api.Contracts.FileOperations;

public sealed record FileOperationPreviewRequestDto(
    FileOperationKind Kind,
    string SourceId,
    IReadOnlyList<string> LogicalPaths,
    string DestinationSourceId,
    string DestinationLogicalDirectory)
{
    internal FileOperationPreviewRequest ToModel() => new(
        Kind,
        SourceId,
        LogicalPaths,
        DestinationSourceId,
        DestinationLogicalDirectory);
}

public sealed record FileOperationConflictResolutionDto(
    Guid ConflictId,
    FileOperationConflictDecision Decision)
{
    internal FileOperationConflictResolution ToModel() => new(ConflictId, Decision);
}

public sealed record FileOperationSubmissionDto(
    Guid PlanId,
    IReadOnlyList<FileOperationConflictResolutionDto> Resolutions)
{
    internal FileOperationSubmission ToModel() => new(
        PlanId,
        Resolutions.Select(resolution => resolution.ToModel()).ToArray());
}

public sealed record FileOperationConflictDto(
    Guid ConflictId,
    string SourceLogicalPath,
    string DestinationLogicalPath,
    FileEntryType SourceType,
    FileEntryType DestinationType,
    IReadOnlyList<FileOperationConflictDecision> AllowedDecisions)
{
    internal static FileOperationConflictDto FromModel(FileOperationConflict model) => new(
        model.ConflictId,
        model.SourceLogicalPath,
        model.DestinationLogicalPath,
        model.SourceType,
        model.DestinationType,
        model.AllowedDecisions);
}

public sealed record FileOperationPreviewDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    FileOperationKind Kind,
    string SourceId,
    IReadOnlyList<string> LogicalPaths,
    string DestinationSourceId,
    string DestinationLogicalDirectory,
    int TotalItems,
    long? TotalBytes,
    IReadOnlyList<FileOperationConflictDto> Conflicts,
    IReadOnlyList<string> Warnings)
{
    internal static FileOperationPreviewDto FromModel(FileOperationPreview model) => new(
        model.PlanId,
        model.ExpiresAt,
        model.Kind,
        model.SourceId,
        model.LogicalPaths,
        model.DestinationSourceId,
        model.DestinationLogicalDirectory,
        model.TotalItems,
        model.TotalBytes,
        model.Conflicts.Select(FileOperationConflictDto.FromModel).ToArray(),
        model.Warnings);
}

public sealed record FileOperationProgressDto(
    string? CurrentLogicalName,
    int CompletedItems,
    int TotalItems,
    long CompletedBytes,
    long? TotalBytes,
    double? Percentage,
    long? BytesPerSecond,
    TimeSpan Elapsed,
    TimeSpan? EstimatedRemaining)
{
    internal static FileOperationProgressDto FromModel(FileOperationProgress model) => new(
        model.CurrentLogicalName,
        model.CompletedItems,
        model.TotalItems,
        model.CompletedBytes,
        model.TotalBytes,
        model.Percentage,
        model.BytesPerSecond,
        model.Elapsed,
        model.EstimatedRemaining);
}

public sealed record FileOperationItemOutcomeDto(
    string SourceId,
    string SourceLogicalPath,
    string? DestinationSourceId,
    string? DestinationLogicalPath,
    FileOperationItemResult Result,
    string? ErrorCode,
    string? Detail)
{
    internal static FileOperationItemOutcomeDto FromModel(FileOperationItemOutcome model) => new(
        model.SourceId,
        model.SourceLogicalPath,
        model.DestinationSourceId,
        model.DestinationLogicalPath,
        model.Result,
        model.ErrorCode,
        model.Detail);
}

public sealed record FileOperationStatusDto(
    Guid OperationId,
    FileOperationKind Kind,
    FileOperationPhase Phase,
    int QueuePosition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    FileOperationProgressDto Progress,
    IReadOnlyList<FileOperationItemOutcomeDto> Outcomes,
    IReadOnlyList<string> Warnings,
    bool Acknowledged)
{
    internal static FileOperationStatusDto FromModel(FileOperationStatus model) => new(
        model.OperationId,
        model.Kind,
        model.Phase,
        model.QueuePosition,
        model.CreatedAt,
        model.UpdatedAt,
        FileOperationProgressDto.FromModel(model.Progress),
        model.Outcomes.Select(FileOperationItemOutcomeDto.FromModel).ToArray(),
        model.Warnings,
        model.Acknowledged);
}
