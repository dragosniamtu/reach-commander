using ReachCommander.Application.FileOperations;
using ReachCommander.Domain.Files;

namespace ReachCommander.Application.Trash;

public enum DeleteMode
{
    Trash,
    Permanent,
}

public sealed record DeletePreviewRequest(
    string SourceId,
    IReadOnlyList<string> LogicalPaths,
    DeleteMode Mode);

public sealed record DeletePreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    DeleteMode Mode,
    bool TrashAvailable,
    string? TrashUnavailableReason,
    int TotalItems,
    long? TotalBytes);

public sealed record DeleteSubmission(
    Guid PlanId,
    bool PermanentDeleteConfirmed);

public sealed record TrashEntry(
    Guid TrashId,
    string SourceId,
    string OriginalLogicalPath,
    string Name,
    FileEntryType Type,
    long? Size,
    DateTimeOffset DeletedAt);

public sealed record RestorePreviewRequest(IReadOnlyList<Guid> TrashIds);

public sealed record RestorePreview(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TrashEntry> Entries,
    IReadOnlyList<FileOperationConflict> Conflicts,
    IReadOnlyList<string> ParentsToCreate);

public sealed record RestoreSubmission(
    Guid PlanId,
    IReadOnlyList<FileOperationConflictResolution> Resolutions);

public sealed record TrashPermanentDeleteRequest(
    IReadOnlyList<Guid> TrashIds,
    bool PermanentDeleteConfirmed);

public sealed record EmptyTrashRequest(
    string? SourceId,
    bool PermanentDeleteConfirmed);

public static class PermanentDeleteConfirmation
{
    public const string Warning =
        "This deletion is permanent, cannot be undone, and is unrecoverable.";
}
