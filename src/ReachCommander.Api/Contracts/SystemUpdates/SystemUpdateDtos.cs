using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Api.Contracts.SystemUpdates;

public sealed record SystemUpdateStatusDto(
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
    DateTimeOffset UpdatedAt)
{
    public static SystemUpdateStatusDto FromModel(SystemUpdateStatus status) => new(
        status.ProtocolVersion,
        status.Supported,
        status.Channel,
        status.CurrentVersion,
        status.TargetVersion,
        status.Phase,
        status.ProgressStage,
        status.UpdateAvailable,
        status.CanApply,
        status.ReasonCode,
        status.Detail,
        status.OperationId,
        status.LastCheckedAt,
        status.UpdatedAt);
}
