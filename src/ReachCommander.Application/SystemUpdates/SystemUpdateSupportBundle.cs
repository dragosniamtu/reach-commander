namespace ReachCommander.Application.SystemUpdates;

public enum SystemUpdateDiagnosticStatus
{
    Healthy,
    Warning,
    Failed,
    TimedOut,
    Unavailable,
    NotApplicable,
}

public sealed record SystemUpdateDiagnosticCheck(
    string Name,
    SystemUpdateDiagnosticStatus Status,
    string ReasonCode);

public sealed record SystemUpdateDiagnosticsSnapshot(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    bool Complete,
    int? UpdaterProtocolVersion,
    string? Channel,
    string? CurrentVersion,
    string? OperationId,
    SystemUpdateTrace? Trace,
    IReadOnlyList<SystemUpdateDiagnosticCheck> Checks);

public sealed record SystemUpdateSupportBundle(string FileName, byte[] Content);

public interface ISystemUpdateSupportBundleService
{
    Task<SystemUpdateSupportBundle> CreateAsync(CancellationToken cancellationToken);
}

public static class SystemUpdateDiagnostics
{
    public static readonly IReadOnlyList<string> CheckNames =
    [
        "dockerEngine",
        "dockerCompose",
        "deploymentFiles",
        "managementCommand",
        "updateTransactions",
        "sourceConfiguration",
        "sourceAccessibility",
        "applicationData",
        "updateChannel",
        "versionState",
        "imageConsistency",
        "containerHealth",
        "updaterService",
        "updaterSocket",
        "installDiskSpace",
        "dockerDiskSpace",
    ];

    public static string ReasonCode(string name, SystemUpdateDiagnosticStatus status)
    {
        if (!CheckNames.Contains(name, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(name));
        }

        return $"{ToSnakeCase(name)}_{ToSnakeCase(status.ToString())}";
    }

    private static string ToSnakeCase(string value) => string.Concat(
        value.SelectMany(character => char.IsUpper(character)
            ? new[] { '_', char.ToLowerInvariant(character) }
            : new[] { character })).TrimStart('_');

}
