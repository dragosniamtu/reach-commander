namespace ReachCommander.Application.SourceManagement;

public abstract class SourceManagementException(string code, string publicDetail)
    : Exception(publicDetail)
{
    public string Code { get; } = code;

    public string PublicDetail { get; } = publicDetail;
}

public sealed class SourceManagementUnavailableException()
    : SourceManagementException(
        "source_management_unavailable",
        "Source management is unavailable on this installation.");

public sealed class SourceManagementProtocolIncompatibleException()
    : SourceManagementException(
        "source_management_installer_upgrade_required",
        "Source management requires the latest installer for Ubuntu to be run once.");

public sealed class SourceManagementBusyException()
    : SourceManagementException(
        "source_management_busy",
        "Another system configuration operation is in progress.");

public sealed class SourceManagementBlockedException()
    : SourceManagementException(
        "source_management_blocked_by_operations",
        "Source management is waiting for file operations to finish.");

public sealed class SourceManagementValidationException()
    : SourceManagementException(
        "source_management_validation_failed",
        "The source folder could not be accepted.");

public sealed class SourceManagementFailedException()
    : SourceManagementException(
        "source_management_failed",
        "The source-management operation could not be completed.");
