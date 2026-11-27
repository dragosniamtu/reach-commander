using System.ComponentModel.DataAnnotations;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Api.Contracts.MediaPreviews;

public sealed record CreateMediaPreviewRequestDto(
    [Required] string SourceId,
    [Required] string VideoPath)
{
    public CreateMediaPreviewCommand ToCommand() => new(SourceId, VideoPath);
}

public sealed record SelectMediaPreviewSubtitleRequestDto(
    [Required] string SubtitlePath,
    TextEncodingKind SourceEncoding = TextEncodingKind.Auto,
    bool EncodingReviewed = false);

public sealed record CreateSubtitleSavePlanRequestDto(long OffsetMilliseconds, double TimeScale = 1);

public sealed record SubtitleCueDto(
    int Index,
    long StartMilliseconds,
    long EndMilliseconds,
    string Text)
{
    public static SubtitleCueDto FromModel(SubtitleCue cue) => new(
        cue.Index,
        cue.StartMilliseconds,
        cue.EndMilliseconds,
        cue.Text);
}

public sealed record MediaPreviewDto(
    Guid SessionId,
    MediaPreviewPhase Phase,
    MediaPlaybackMode PlaybackMode,
    string VideoName,
    string VideoPath,
    long? DurationMilliseconds,
    string? SubtitlePath,
    IReadOnlyList<SubtitleCueDto> Cues,
    bool SourceReadOnly,
    DateTimeOffset ExpiresAt,
    string? FailureCode,
    string? FailureDetail,
    bool TranscodeActive,
    SubtitleEncodingInfo? SubtitleEncoding = null,
    string? SubtitleErrorCode = null)
{
    public static MediaPreviewDto FromModel(MediaPreviewSession session) => new(
        session.SessionId,
        session.Phase,
        session.PlaybackMode,
        session.VideoName,
        session.VideoPath,
        session.DurationMilliseconds,
        session.SubtitlePath,
        session.Cues.Select(SubtitleCueDto.FromModel).ToArray(),
        session.SourceReadOnly,
        session.ExpiresAt,
        session.FailureCode,
        session.FailureDetail,
        session.TranscodeActive,
        session.SubtitleEncoding,
        session.SubtitleErrorCode);
}

public sealed record SubtitleSavePlanDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    string SubtitlePath,
    string BackupPath,
    long OffsetMilliseconds,
    bool CanExecute,
    double TimeScale = 1)
{
    public static SubtitleSavePlanDto FromModel(SubtitleSavePlan plan) => new(
        plan.PlanId,
        plan.ExpiresAt,
        plan.SubtitlePath,
        plan.BackupPath,
        plan.OffsetMilliseconds,
        plan.CanExecute,
        plan.TimeScale);
}

public sealed record SubtitleSaveResultDto(
    string SubtitlePath,
    string BackupPath,
    bool RecoveryRequired)
{
    public static SubtitleSaveResultDto FromModel(SubtitleSaveResult result) => new(
        result.SubtitlePath,
        result.BackupPath,
        result.RecoveryRequired);
}
