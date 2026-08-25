using ReachCommander.Application.Trash;

namespace ReachCommander.Application.FileOperations;

public abstract class FileOperationException(string code, string publicDetail)
    : Exception(publicDetail)
{
    public string Code { get; } = code;

    public string PublicDetail { get; } = publicDetail;
}

public sealed class OperationSourceReadOnlyException()
    : FileOperationException("source_read_only", "The source is read-only.");

public sealed class OperationSourceUnavailableException()
    : FileOperationException("source_unavailable", "The source is unavailable.");

public sealed class DestinationUnavailableException()
    : FileOperationException("destination_unavailable", "The destination is unavailable.");

public sealed class InvalidOperationSelectionException()
    : FileOperationException(
        "invalid_operation_selection",
        "The selected entries cannot be used for this operation.");

public sealed class InvalidDirectoryNameException()
    : FileOperationException("invalid_directory_name", "The directory name is invalid.");

public sealed class UnsafeSymbolicLinkException()
    : FileOperationException(
        "unsafe_symbolic_link",
        "Symbolic links, junctions, and reparse points are not supported.");

public sealed class OperationPlanNotFoundException()
    : FileOperationException("operation_plan_not_found", "The operation plan was not found.");

public sealed class OperationPlanExpiredException()
    : FileOperationException(
        "operation_plan_expired",
        "The operation plan expired. Preview the operation again.");

public sealed class OperationPlanStaleException()
    : FileOperationException(
        "operation_plan_stale",
        "Files changed after preview. Preview the operation again.");

public sealed class DestinationConflictException()
    : FileOperationException(
        "destination_conflict",
        "The destination changed after preview. Preview the operation again.");

public sealed class InsufficientStorageException()
    : FileOperationException(
        "insufficient_storage",
        "The destination does not have enough available storage.");

public sealed class FileOperationCancelledException()
    : FileOperationException("operation_cancelled", "The operation was cancelled.");

public sealed class FileOperationInterruptedException()
    : FileOperationException(
        "operation_interrupted",
        "The operation was interrupted by a server restart.");

public sealed class MoveSourceNotRemovedException()
    : FileOperationException(
        "move_source_not_removed",
        "The item was copied but the source could not be removed.");

public sealed class TrashUnavailableException()
    : FileOperationException(
        "trash_unavailable",
        "Managed Trash is unavailable for the selected entries.");

public sealed class TrashManifestInvalidException()
    : FileOperationException("trash_manifest_invalid", "The Trash record is invalid.");

public sealed class TrashRestoreConflictException()
    : FileOperationException(
        "trash_restore_conflict",
        "The restore destination changed after preview. Preview the restore again.");

public sealed class PermanentDeleteConfirmationRequiredException()
    : FileOperationException(
        "permanent_delete_confirmation_required",
        PermanentDeleteConfirmation.Warning);
