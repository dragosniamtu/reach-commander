using ReachCommander.Api.Contracts.FileOperations;
using ReachCommander.Application.Trash;
using ReachCommander.Domain.Files;

namespace ReachCommander.Api.Contracts.Trash;

public sealed record DeletePreviewRequestDto(
    string SourceId,
    IReadOnlyList<string> LogicalPaths,
    DeleteMode Mode)
{
    internal DeletePreviewRequest ToModel() => new(SourceId, LogicalPaths, Mode);
}

public sealed record DeletePreviewDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    DeleteMode Mode,
    bool TrashAvailable,
    string? TrashUnavailableReason,
    int TotalItems,
    long? TotalBytes)
{
    internal static DeletePreviewDto FromModel(DeletePreview model) => new(
        model.PlanId,
        model.ExpiresAt,
        model.Mode,
        model.TrashAvailable,
        model.TrashUnavailableReason,
        model.TotalItems,
        model.TotalBytes);
}

public sealed record DeleteSubmissionDto(Guid PlanId, bool PermanentDeleteConfirmed)
{
    internal DeleteSubmission ToModel() => new(PlanId, PermanentDeleteConfirmed);
}

public sealed record TrashEntryDto(
    Guid TrashId,
    string SourceId,
    string OriginalLogicalPath,
    string Name,
    FileEntryType Type,
    long? Size,
    DateTimeOffset DeletedAt)
{
    internal static TrashEntryDto FromModel(TrashEntry model) => new(
        model.TrashId,
        model.SourceId,
        model.OriginalLogicalPath,
        model.Name,
        model.Type,
        model.Size,
        model.DeletedAt);
}

public sealed record RestorePreviewRequestDto(IReadOnlyList<Guid> TrashIds)
{
    internal RestorePreviewRequest ToModel() => new(TrashIds);
}

public sealed record RestorePreviewDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<TrashEntryDto> Entries,
    IReadOnlyList<FileOperationConflictDto> Conflicts,
    IReadOnlyList<string> ParentsToCreate)
{
    internal static RestorePreviewDto FromModel(RestorePreview model) => new(
        model.PlanId,
        model.ExpiresAt,
        model.Entries.Select(TrashEntryDto.FromModel).ToArray(),
        model.Conflicts.Select(FileOperationConflictDto.FromModel).ToArray(),
        model.ParentsToCreate);
}

public sealed record RestoreSubmissionDto(
    Guid PlanId,
    IReadOnlyList<FileOperationConflictResolutionDto> Resolutions)
{
    internal RestoreSubmission ToModel() => new(
        PlanId,
        Resolutions.Select(resolution => resolution.ToModel()).ToArray());
}

public sealed record TrashPermanentDeleteRequestDto(
    IReadOnlyList<Guid> TrashIds,
    bool PermanentDeleteConfirmed)
{
    internal TrashPermanentDeleteRequest ToModel() => new(
        TrashIds,
        PermanentDeleteConfirmed);
}

public sealed record EmptyTrashRequestDto(
    string? SourceId,
    bool PermanentDeleteConfirmed)
{
    internal EmptyTrashRequest ToModel() => new(SourceId, PermanentDeleteConfirmed);
}
