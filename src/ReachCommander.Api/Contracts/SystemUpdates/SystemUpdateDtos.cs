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
    DateTimeOffset UpdatedAt,
    SystemUpdateTraceDto? Trace)
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
        status.UpdatedAt,
        status.Trace is null ? null : SystemUpdateTraceDto.FromModel(status.Trace));
}

public sealed record SystemUpdateTraceDto(
    DateTimeOffset StartedAt,
    long ElapsedSeconds,
    DateTimeOffset? LastActivityAt,
    IReadOnlyList<SystemUpdateTraceEventDto> Events)
{
    public static SystemUpdateTraceDto FromModel(SystemUpdateTrace trace) => new(
        trace.StartedAt,
        trace.ElapsedSeconds,
        trace.LastActivityAt,
        trace.Events.Select(SystemUpdateTraceEventDto.FromModel).ToArray());
}

public sealed record SystemUpdateTraceEventDto(
    int Sequence,
    DateTimeOffset Timestamp,
    long ElapsedSeconds,
    SystemUpdateTraceEventCode Code,
    SystemUpdateProgressStage? Stage,
    SystemUpdateTraceOutcome Outcome)
{
    public static SystemUpdateTraceEventDto FromModel(SystemUpdateTraceEvent traceEvent) => new(
        traceEvent.Sequence,
        traceEvent.Timestamp,
        traceEvent.ElapsedSeconds,
        traceEvent.Code,
        traceEvent.Stage,
        traceEvent.Outcome);
}
