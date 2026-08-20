using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using ReachCommander.Application.Archives;
using ReachCommander.Domain.Archives;

namespace ReachCommander.Api.Contracts.Archives;

public sealed record ArchiveEntryDto(
    string Path,
    string Name,
    string Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string? Extension,
    string Attributes)
{
    public static ArchiveEntryDto FromEntry(ArchiveEntry entry) => new(
        entry.Path,
        entry.Name,
        entry.Type == ArchiveEntryType.Directory ? "directory" : "file",
        entry.Size,
        entry.ModifiedAt,
        entry.Extension,
        entry.Attributes);
}

public sealed record ArchiveDirectoryDto(
    string SourceId,
    string ArchivePath,
    string Path,
    string Format,
    int VolumeCount,
    bool IsReadOnly,
    IReadOnlyList<ArchiveEntryDto> Entries)
{
    public static ArchiveDirectoryDto FromListing(ArchiveDirectoryListing listing) => new(
        listing.Location.SourceId,
        listing.Location.ArchivePath,
        listing.Location.InternalPath,
        listing.Format switch
        {
            ArchiveFormat.Zip => "zip",
            ArchiveFormat.Rar => "rar",
            ArchiveFormat.SevenZip => "sevenZip",
            _ => throw new ArgumentOutOfRangeException(nameof(listing)),
        },
        listing.VolumeCount,
        true,
        Array.AsReadOnly(listing.Entries.Select(ArchiveEntryDto.FromEntry).ToArray()));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ArchiveExtractionPreviewRequestDto(
    [param: Required] string? SourceId,
    [param: Required] string? ArchivePath,
    [param: Required] string? InternalDirectory,
    [param: Required] IReadOnlyList<string>? EntryPaths,
    bool ExtractAll,
    [param: Required] string? DestinationSourceId,
    [param: Required] string? DestinationPath) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (value, member) in new[]
                 {
                     (SourceId, nameof(SourceId)),
                     (ArchivePath, nameof(ArchivePath)),
                     (InternalDirectory, nameof(InternalDirectory)),
                     (DestinationSourceId, nameof(DestinationSourceId)),
                     (DestinationPath, nameof(DestinationPath)),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield return new ValidationResult(
                    $"{member} must not be blank.",
                    [member]);
            }
        }

        if (EntryPaths is null)
        {
            yield break;
        }

        if (EntryPaths.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult(
                "Entry paths must not be blank.",
                [nameof(EntryPaths)]);
        }

        if (EntryPaths.Distinct(StringComparer.Ordinal).Count() != EntryPaths.Count)
        {
            yield return new ValidationResult(
                "Entry paths must not contain duplicates.",
                [nameof(EntryPaths)]);
        }

        if (ExtractAll)
        {
            if (!string.Equals(InternalDirectory, "/", StringComparison.Ordinal) || EntryPaths.Count != 0)
            {
                yield return new ValidationResult(
                    "Extract-all mode requires the archive root and no selected entry paths.",
                    [nameof(ExtractAll), nameof(InternalDirectory), nameof(EntryPaths)]);
            }
        }
        else if (EntryPaths.Count == 0)
        {
            yield return new ValidationResult(
                "Selected-entry mode requires at least one entry path.",
                [nameof(ExtractAll), nameof(EntryPaths)]);
        }
    }

    public ArchiveExtractionPreviewRequest ToModel() => new(
        SourceId!,
        ArchivePath!,
        InternalDirectory!,
        EntryPaths!,
        ExtractAll,
        DestinationSourceId!,
        DestinationPath!);
}

public sealed record ArchiveExtractionIssueDto(
    string Code,
    string Message,
    IReadOnlyList<string> LogicalPaths)
{
    public static ArchiveExtractionIssueDto FromModel(ArchiveExtractionIssue issue) => new(
        issue.Code,
        issue.Message,
        Array.AsReadOnly(issue.LogicalPaths.ToArray()));
}

public sealed record ArchiveExtractionPreviewDto(
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
    IReadOnlyList<ArchiveExtractionIssueDto> Conflicts,
    IReadOnlyList<ArchiveExtractionIssueDto> Violations,
    bool CanExecute)
{
    public static ArchiveExtractionPreviewDto FromModel(ArchiveExtractionPreview preview) => new(
        preview.PlanId,
        preview.ExpiresAt,
        preview.Format,
        preview.VolumeCount,
        Array.AsReadOnly(preview.SelectedRoots.ToArray()),
        preview.FileCount,
        preview.DirectoryCount,
        preview.TotalExtractedBytes,
        preview.DestinationSourceId,
        preview.DestinationPath,
        Array.AsReadOnly(preview.Conflicts.Select(ArchiveExtractionIssueDto.FromModel).ToArray()),
        Array.AsReadOnly(preview.Violations.Select(ArchiveExtractionIssueDto.FromModel).ToArray()),
        preview.CanExecute);
}

public sealed record ArchiveExtractionOperationDto(
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
    string? ErrorDetail)
{
    public static ArchiveExtractionOperationDto FromModel(ArchiveExtractionOperation operation) => new(
        operation.OperationId,
        operation.State,
        operation.CompletedFiles,
        operation.TotalFiles,
        operation.ExtractedBytes,
        operation.TotalBytes,
        operation.Percent,
        operation.CurrentEntryName,
        operation.CanCancel,
        operation.CompensationState,
        Array.AsReadOnly(operation.RecoveryNames.ToArray()),
        operation.ErrorCode,
        operation.ErrorDetail);
}
