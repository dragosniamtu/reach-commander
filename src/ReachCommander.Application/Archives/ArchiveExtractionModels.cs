using ReachCommander.Domain.Archives;

namespace ReachCommander.Application.Archives;

public sealed record ArchiveExtractionPreviewRequest(
    string SourceId,
    string ArchivePath,
    string InternalDirectory,
    IReadOnlyList<string> EntryPaths,
    bool ExtractAll,
    string DestinationSourceId,
    string DestinationPath);

public sealed record ArchiveExtractionIssue(
    string Code,
    string Message,
    IReadOnlyList<string> LogicalPaths);

public sealed record ArchiveExtractionPreview(
    string PlanId,
    DateTimeOffset ExpiresAt,
    ArchiveFormat Format,
    int VolumeCount,
    IReadOnlyList<string> SelectedRoots,
    int FileCount,
    int DirectoryCount,
    long? TotalExtractedBytes,
    string DestinationSourceId,
    string DestinationPath,
    IReadOnlyList<ArchiveExtractionIssue> Conflicts,
    IReadOnlyList<ArchiveExtractionIssue> Violations,
    bool CanExecute);

public enum ArchiveExtractionState
{
    Queued,
    Extracting,
    Finalizing,
    Completed,
    Cancelled,
    Failed,
    RecoveryRequired,
}

public enum ArchiveCompensationState
{
    NotRequired,
    NotStarted,
    Succeeded,
    Failed,
}

public sealed record ArchiveExtractionOperation(
    string OperationId,
    ArchiveExtractionState State,
    int CompletedFiles,
    int TotalFiles,
    long ExtractedBytes,
    long? TotalBytes,
    double? Percent,
    string? CurrentEntryName,
    bool CanCancel,
    ArchiveCompensationState CompensationState,
    IReadOnlyList<string> RecoveryNames,
    string? ErrorCode,
    string? ErrorDetail);
