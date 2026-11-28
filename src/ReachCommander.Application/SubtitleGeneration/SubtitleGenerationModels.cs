using System.Text.Json.Serialization;

namespace ReachCommander.Application.SubtitleGeneration;

public sealed record SubtitleGenerationRequest(
    string SourceId, string Path, bool English = true, bool Romanian = false, bool SkipExisting = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Paths = null)
{
    public const int MaximumPathLength = 4096;
}

public sealed record SubtitleGenerationCapabilities(bool Enabled, bool WorkerAvailable, bool EnglishAvailable,
    bool RomanianAvailable, string? ReasonCode, string? Detail);

public enum SubtitleJobPhase { Queued, Discovering, Running, Cancelling, Completed, Cancelled, Failed, Interrupted }
public enum SubtitleFilePhase { Pending, Running, Completed, Skipped, Failed, Cancelled }

public sealed record SubtitleFileStatus(string Path, SubtitleFilePhase Phase, string? Error = null);

public sealed record SubtitleJobStatus(
    Guid JobId, string SourceId, string Path, bool English, bool Romanian,
    SubtitleJobPhase Phase, string Stage, string? CurrentPath, int CurrentPercent,
    IReadOnlyList<SubtitleFileStatus> Files, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? Error = null);

public interface ISubtitleGenerationService
{
    Task<SubtitleGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken);
    Task<SubtitleJobStatus> SubmitAsync(SubtitleGenerationRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SubtitleJobStatus>> ListAsync(CancellationToken cancellationToken);
    Task<SubtitleJobStatus> GetAsync(Guid jobId, CancellationToken cancellationToken);
    Task<SubtitleJobStatus> CancelAsync(Guid jobId, CancellationToken cancellationToken);
}

public sealed class SubtitleGenerationException(string code, string publicDetail) : Exception(publicDetail)
{
    public string Code { get; } = code;
    public string PublicDetail { get; } = publicDetail;
}
