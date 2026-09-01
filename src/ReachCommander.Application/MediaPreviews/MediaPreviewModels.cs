namespace ReachCommander.Application.MediaPreviews;

public sealed record SubtitleCue(
    int Index,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text);

public enum MediaPreviewPhase
{
    Probing,
    Transcoding,
    Ready,
    Failed,
}

public enum MediaPlaybackMode
{
    Direct,
    Hls,
}

public sealed record CreateMediaPreviewCommand(
    string SourceId,
    string VideoPath);

public sealed record MediaPreviewSession(
    Guid SessionId,
    MediaPreviewPhase Phase,
    MediaPlaybackMode PlaybackMode,
    string VideoName,
    string VideoPath,
    long? DurationMilliseconds,
    string? SubtitlePath,
    IReadOnlyList<SubtitleCue> Cues,
    bool SourceReadOnly,
    DateTimeOffset ExpiresAt,
    string? FailureCode = null,
    string? FailureDetail = null);

public sealed record MediaAsset(
    Stream Content,
    string ContentType,
    long Length,
    bool EnableRanges);

public sealed record SubtitleSavePlan(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    string SubtitlePath,
    string BackupPath,
    long OffsetMilliseconds,
    bool CanExecute);

public sealed record SubtitleSaveResult(
    string SubtitlePath,
    string BackupPath,
    bool RecoveryRequired);
