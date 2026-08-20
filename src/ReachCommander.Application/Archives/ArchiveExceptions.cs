namespace ReachCommander.Application.Archives;

public abstract class ArchiveException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;

    public string Detail { get; } = detail;
}

public sealed class ArchiveUnsupportedException()
    : ArchiveException("archive_unsupported", "This archive format is not supported.");

public sealed class ArchiveInvalidException()
    : ArchiveException("archive_invalid", "The archive signature or structure is invalid.");

public sealed class ArchiveEncryptedException()
    : ArchiveException("archive_encrypted", "Encrypted archives are not supported.");

public sealed class ArchiveVolumeSecondaryException(string primaryLogicalPath)
    : ArchiveException(
        "archive_volume_secondary",
        $"Open the primary archive volume '{primaryLogicalPath}'.")
{
    public string PrimaryLogicalPath { get; } = primaryLogicalPath;
}

public sealed class ArchiveVolumeSetInvalidException(IEnumerable<string> expectedLogicalNames)
    : ArchiveException(
        "archive_volume_set_invalid",
        "The archive volume set is incomplete or inconsistent.")
{
    public IReadOnlyList<string> ExpectedLogicalNames { get; } =
        Array.AsReadOnly(expectedLogicalNames.ToArray());
}

public sealed class ArchiveEntryUnsafeException()
    : ArchiveException("archive_entry_unsafe", "The archive contains an unsafe entry.");

public sealed class ArchiveLimitExceededException(string detail)
    : ArchiveException("archive_limit_exceeded", detail);

public sealed class ArchiveDestinationInvalidException()
    : ArchiveException(
        "archive_destination_invalid",
        "Choose an available filesystem directory as the extraction destination.");

public sealed class ArchiveDestinationReadOnlyException(string sourceId)
    : ArchiveException(
        "archive_destination_read_only",
        $"Source '{sourceId}' does not allow archive extraction.")
{
    public string SourceId { get; } = sourceId;
}

public sealed class ArchiveDestinationConflictException(IEnumerable<string> logicalNames)
    : ArchiveException(
        "archive_destination_conflict",
        "One or more extraction destination names already exist.")
{
    public IReadOnlyList<string> LogicalNames { get; } =
        Array.AsReadOnly(logicalNames.ToArray());
}

public sealed class ArchivePlanNotFoundException()
    : ArchiveException("archive_plan_not_found", "The archive extraction plan was not found.");

public sealed class ArchivePlanExpiredException()
    : ArchiveException("archive_plan_expired", "The archive extraction plan has expired.");

public sealed class ArchivePlanStaleException()
    : ArchiveException("archive_plan_stale", "The archive changed after preview.");

public sealed class ArchiveDestinationChangedException()
    : ArchiveException(
        "archive_destination_changed",
        "The extraction destination changed after preview.");

public sealed class ArchiveCapacityReachedException()
    : ArchiveException(
        "archive_capacity_reached",
        "The archive extraction capacity is currently reached.");

public sealed class ArchiveWorkerFailedException()
    : ArchiveException("archive_worker_failed", "The isolated archive worker failed.");

public sealed class ArchiveExtractionCancelledException()
    : ArchiveException("archive_extraction_cancelled", "The archive extraction was cancelled.");

public sealed class ArchiveRecoveryRequiredException(IEnumerable<string> recoveryNames)
    : ArchiveException(
        "archive_recovery_required",
        "Archive extraction requires administrator recovery.")
{
    public IReadOnlyList<string> RecoveryNames { get; } =
        Array.AsReadOnly(recoveryNames.ToArray());
}
