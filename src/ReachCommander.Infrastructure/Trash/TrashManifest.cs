using ReachCommander.Domain.Files;
using ReachCommander.Infrastructure.FileOperations.Planning;

namespace ReachCommander.Infrastructure.Trash;

internal sealed record TrashManifest(
    int SchemaVersion,
    Guid TrashId,
    string SourceId,
    string OriginalLogicalPath,
    string OriginalName,
    FileEntryType Type,
    long? Size,
    DateTimeOffset DeletedAt,
    string StoredRelativeItemPath,
    FileOperationEntryFingerprint Fingerprint)
{
    internal const int CurrentSchemaVersion = 1;
}

internal sealed record TrashCapability(bool IsAvailable, string? UnavailableReason);

internal sealed record TrashStoragePaths(
    string TrashRootPhysicalPath,
    string ManifestPhysicalPath,
    string ItemContainerPhysicalPath,
    string ItemPhysicalPath,
    string StagingContainerPhysicalPath,
    string StagingItemPhysicalPath);

internal sealed record ValidTrashRecord(
    TrashManifest Manifest,
    TrashStoragePaths Paths);
