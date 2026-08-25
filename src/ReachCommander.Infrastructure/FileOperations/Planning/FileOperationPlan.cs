using ReachCommander.Application.FileOperations;
using ReachCommander.Infrastructure.Mutations;

namespace ReachCommander.Infrastructure.FileOperations.Planning;

internal sealed record PlannedFileOperationEntry(
    string SourceLogicalPath,
    string DestinationLogicalPath,
    string TopLevelSourceLogicalPath,
    FileOperationEntryFingerprint Fingerprint,
    FileOperationEntryFingerprint? DestinationFingerprint,
    Guid? ConflictId,
    bool IsTopLevel);

internal sealed record FileOperationPlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    FileOperationKind Kind,
    string? SourceId,
    IReadOnlyList<string> SourceLogicalPaths,
    string? DestinationSourceId,
    string? DestinationLogicalDirectory,
    IReadOnlyList<PlannedFileOperationEntry> Entries,
    IReadOnlyList<Guid> TrashIds,
    string? TrashSourceScope,
    IReadOnlyList<FileOperationConflict> Conflicts,
    IReadOnlyList<DirectoryMutationTarget> LockTargets,
    long? TotalBytes);
