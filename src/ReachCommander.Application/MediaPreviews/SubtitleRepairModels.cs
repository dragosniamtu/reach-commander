namespace ReachCommander.Application.MediaPreviews;

public sealed record SubtitleRepairCapabilities(bool Enabled, bool Available, string? ReasonCode = null, string? Detail = null);

public sealed record SubtitleRepairStatus(
    string State, string Stage, int Percent = 0, long? OffsetMilliseconds = null,
    double? TimeScale = null, string? ErrorCode = null);

public interface ISubtitleRepairService
{
    bool HasActiveOperations { get; }
    Task<SubtitleRepairCapabilities> GetCapabilitiesAsync(CancellationToken token);
    Task<SubtitleRepairStatus> StartAsync(Guid sessionId, CancellationToken token);
    ValueTask<SubtitleRepairStatus> GetAsync(Guid sessionId, CancellationToken token);
    Task<SubtitleRepairStatus> CancelAsync(Guid sessionId, CancellationToken token);
}
