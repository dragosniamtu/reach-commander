namespace ReachCommander.Application.SourceManagement;

public enum SourceAccess
{
    ReadOnly,
    ReadWrite,
}

public enum SourceManagementPhase
{
    Accepted,
    Validating,
    Applying,
    Restarting,
    HealthChecking,
    Completed,
    RolledBack,
    Failed,
}

public sealed record SourceManagementCapability(
    bool Supported,
    string ReasonCode,
    string Detail);

public sealed record SourceAddRequest(
    string DisplayName,
    string HostPath,
    SourceAccess Access);

public sealed record SourceManagementOperation(
    Guid OperationId,
    string? SourceId,
    string? DisplayName,
    SourceManagementPhase Phase,
    string ReasonCode,
    string Detail,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminal => Phase is
        SourceManagementPhase.Completed or
        SourceManagementPhase.RolledBack or
        SourceManagementPhase.Failed;
}
