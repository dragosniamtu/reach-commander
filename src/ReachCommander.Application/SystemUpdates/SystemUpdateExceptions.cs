namespace ReachCommander.Application.SystemUpdates;

public abstract class SystemUpdateException(string code, string publicDetail)
    : Exception(publicDetail)
{
    public string Code { get; } = code;

    public string PublicDetail { get; } = publicDetail;
}

public sealed class SystemUpdateUnavailableException()
    : SystemUpdateException(
        "system_update_unavailable",
        "System updates are unavailable on this installation.");

public sealed class SystemUpdateProtocolIncompatibleException()
    : SystemUpdateException(
        "system_update_protocol_incompatible",
        "The installed host updater is incompatible. Refresh the Ubuntu installer bundle.");

public sealed class SystemUpdateCheckRateLimitedException()
    : SystemUpdateException(
        "system_update_check_rate_limited",
        "Updates were checked recently. Try again shortly.");

public sealed class SystemUpdateBlockedByOperationsException()
    : SystemUpdateException(
        "system_update_blocked_by_operations",
        "The update is waiting for file operations to finish.");

public sealed class SystemUpdateInProgressException()
    : SystemUpdateException(
        "system_update_in_progress",
        "ReachCommander is already applying an update.");

public sealed class SystemUpdateFailedException()
    : SystemUpdateException(
        "system_update_failed",
        "The update requires administrator attention.");
