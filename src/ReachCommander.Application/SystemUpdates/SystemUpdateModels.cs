namespace ReachCommander.Application.SystemUpdates;

public enum SystemUpdatePhase
{
    Unavailable,
    Checking,
    Current,
    Available,
    Blocked,
    Applying,
    Completed,
    RolledBack,
    Failed,
}

public enum SystemUpdateProgressStage
{
    Downloading,
    Installing,
    Restarting,
    HealthChecking,
    Restoring,
    RestartingPrevious,
    VerifyingRecovery,
}

public sealed record SystemUpdateStatus(
    int ProtocolVersion,
    bool Supported,
    string? Channel,
    string? CurrentVersion,
    string? TargetVersion,
    SystemUpdatePhase Phase,
    SystemUpdateProgressStage? ProgressStage,
    bool UpdateAvailable,
    bool CanApply,
    string? ReasonCode,
    string? Detail,
    string? OperationId,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset UpdatedAt);

public static class SystemUpdateStatusFactory
{
    public const int ProtocolVersion = 1;
    public const int MaximumDetailLength = 240;

    public static SystemUpdateStatus Unavailable(
        DateTimeOffset now,
        string reasonCode = "system_update_unavailable",
        string detail = "System updates are unavailable on this installation.") =>
        Create(
            supported: false,
            channel: null,
            currentVersion: null,
            targetVersion: null,
            SystemUpdatePhase.Unavailable,
            updateAvailable: false,
            canApply: false,
            reasonCode,
            detail,
            operationId: null,
            lastCheckedAt: null,
            now);

    public static SystemUpdateStatus Incompatible(DateTimeOffset now) =>
        Unavailable(
            now,
            "system_update_protocol_incompatible",
            "The installed host updater is incompatible. Refresh the Ubuntu installer bundle.");

    public static SystemUpdateStatus SupportedUnavailable(
        string? channel,
        string? currentVersion,
        string reasonCode,
        string detail,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now) =>
        Create(
            supported: true,
            channel,
            currentVersion,
            targetVersion: null,
            SystemUpdatePhase.Unavailable,
            updateAvailable: false,
            canApply: false,
            reasonCode,
            detail,
            operationId: null,
            lastCheckedAt,
            now);

    public static SystemUpdateStatus Checking(DateTimeOffset now) =>
        Create(
            supported: true,
            channel: null,
            currentVersion: null,
            targetVersion: null,
            SystemUpdatePhase.Checking,
            updateAvailable: false,
            canApply: false,
            "system_update_checking",
            "Checking for updates.",
            operationId: null,
            lastCheckedAt: null,
            now);

    public static SystemUpdateStatus Current(
        string channel,
        string currentVersion,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            targetVersion: null,
            SystemUpdatePhase.Current,
            updateAvailable: false,
            canApply: false,
            "up_to_date",
            "ReachCommander is up to date.",
            operationId: null,
            lastCheckedAt,
            now);

    public static SystemUpdateStatus Pinned(
        string channel,
        string currentVersion,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            targetVersion: null,
            SystemUpdatePhase.Current,
            updateAvailable: false,
            canApply: false,
            "version_pinned",
            "Updates are disabled while this deployment is version-pinned.",
            operationId: null,
            lastCheckedAt,
            now);

    public static SystemUpdateStatus Available(
        string channel,
        string currentVersion,
        string targetVersion,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            Required(targetVersion, nameof(targetVersion)),
            SystemUpdatePhase.Available,
            updateAvailable: true,
            canApply: true,
            "update_available",
            "A trusted ReachCommander update is available.",
            operationId: null,
            lastCheckedAt,
            now);

    public static SystemUpdateStatus Blocked(
        string channel,
        string currentVersion,
        string targetVersion,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            Required(targetVersion, nameof(targetVersion)),
            SystemUpdatePhase.Blocked,
            updateAvailable: true,
            canApply: false,
            "system_update_blocked_by_operations",
            "The update is waiting for file operations to finish.",
            operationId: null,
            lastCheckedAt,
            now);

    public static SystemUpdateStatus Applying(
        string channel,
        string currentVersion,
        string targetVersion,
        string operationId,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now,
        SystemUpdateProgressStage? progressStage = null) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            Required(targetVersion, nameof(targetVersion)),
            SystemUpdatePhase.Applying,
            updateAvailable: true,
            canApply: false,
            "update_applying",
            "ReachCommander is applying the trusted update.",
            Required(operationId, nameof(operationId)),
            lastCheckedAt,
            now,
            progressStage);

    public static SystemUpdateStatus Completed(
        string channel,
        string currentVersion,
        string targetVersion,
        string operationId,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now,
        SystemUpdateProgressStage? progressStage = null) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            Required(targetVersion, nameof(targetVersion)),
            SystemUpdatePhase.Completed,
            updateAvailable: false,
            canApply: false,
            "update_completed",
            "ReachCommander was updated successfully.",
            Required(operationId, nameof(operationId)),
            lastCheckedAt,
            now,
            progressStage);

    public static SystemUpdateStatus RolledBack(
        string channel,
        string currentVersion,
        string targetVersion,
        string operationId,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now,
        SystemUpdateProgressStage? progressStage = null) =>
        Create(
            supported: true,
            channel,
            Required(currentVersion, nameof(currentVersion)),
            Required(targetVersion, nameof(targetVersion)),
            SystemUpdatePhase.RolledBack,
            updateAvailable: false,
            canApply: false,
            "candidate_rolled_back",
            "The candidate was unhealthy and the previous version was restored.",
            Required(operationId, nameof(operationId)),
            lastCheckedAt,
            now,
            progressStage);

    public static SystemUpdateStatus Failed(
        string? channel,
        string? currentVersion,
        string? targetVersion,
        string? operationId,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now,
        string detail = "The update requires administrator attention.",
        SystemUpdateProgressStage? progressStage = null) =>
        Create(
            supported: true,
            channel,
            currentVersion,
            targetVersion,
            SystemUpdatePhase.Failed,
            updateAvailable: false,
            canApply: false,
            "update_failed",
            detail,
            operationId,
            lastCheckedAt,
            now,
            progressStage);

    private static SystemUpdateStatus Create(
        bool supported,
        string? channel,
        string? currentVersion,
        string? targetVersion,
        SystemUpdatePhase phase,
        bool updateAvailable,
        bool canApply,
        string reasonCode,
        string detail,
        string? operationId,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset now,
        SystemUpdateProgressStage? progressStage = null)
    {
        if (canApply &&
            (phase != SystemUpdatePhase.Available ||
             !updateAvailable ||
             string.IsNullOrWhiteSpace(currentVersion) ||
             string.IsNullOrWhiteSpace(targetVersion)))
        {
            throw new ArgumentException("Only a resolved available update can be applied.");
        }

        if (progressStage is not null && phase is not (
                SystemUpdatePhase.Applying or
                SystemUpdatePhase.Completed or
                SystemUpdatePhase.RolledBack or
                SystemUpdatePhase.Failed))
        {
            throw new ArgumentException("Update progress requires an active or terminal operation.");
        }

        return new SystemUpdateStatus(
            ProtocolVersion,
            supported,
            Optional(channel),
            Optional(currentVersion),
            Optional(targetVersion),
            phase,
            progressStage,
            updateAvailable,
            canApply,
            reasonCode,
            SanitizeDetail(detail),
            Optional(operationId),
            lastCheckedAt,
            now);
    }

    private static string Required(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string? Optional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string SanitizeDetail(string detail)
    {
        var sanitized = detail
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace("/opt/reachcommander", "[host path]", StringComparison.OrdinalIgnoreCase)
            .Replace("sha256:", "digest:", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (sanitized.Length == 0)
        {
            sanitized = "The updater request could not be completed.";
        }

        return sanitized[..Math.Min(sanitized.Length, MaximumDetailLength)];
    }
}
