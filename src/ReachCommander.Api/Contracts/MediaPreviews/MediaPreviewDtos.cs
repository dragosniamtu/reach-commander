using System.ComponentModel.DataAnnotations;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Api.Contracts.MediaPreviews;

public sealed record CreateMediaPreviewRequestDto(
    [Required] string SourceId,
    [Required] string VideoPath)
{
    public CreateMediaPreviewCommand ToCommand() => new(SourceId, VideoPath);
}

public sealed record SelectMediaPreviewSubtitleRequestDto(
    [Required] string SubtitlePath);

public sealed record CreateSubtitleSavePlanRequestDto(long OffsetMilliseconds);

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
    string? FailureDetail)
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
        session.FailureDetail);
}

public sealed record SubtitleSavePlanDto(
    Guid PlanId,
    DateTimeOffset ExpiresAt,
    string SubtitlePath,
    string BackupPath,
    long OffsetMilliseconds,
    bool CanExecute)
{
    public static SubtitleSavePlanDto FromModel(SubtitleSavePlan plan) => new(
        plan.PlanId,
        plan.ExpiresAt,
        plan.SubtitlePath,
        plan.BackupPath,
        plan.OffsetMilliseconds,
        plan.CanExecute);
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
