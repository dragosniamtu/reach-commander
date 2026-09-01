namespace ReachCommander.Application.MediaPreviews;

public sealed class MediaPreviewException(
    string code,
    string publicDetail) : Exception(publicDetail)
{
    public string Code { get; } = code;

    public string PublicDetail { get; } = publicDetail;

    public static MediaPreviewException SubtitleInvalid() => new(
        "subtitle_invalid",
        "The subtitle file is not a valid SRT document.");

    public static MediaPreviewException SubtitleTooLarge() => new(
        "subtitle_too_large",
        "The subtitle file exceeds the configured size or cue limit.");

    public static MediaPreviewException SubtitleEncodingUnsupported() =>
        new MediaPreviewException(
            "subtitle_encoding_unsupported",
            "The subtitle file must use UTF-8 or BOM-marked UTF-16 encoding.");

    public static MediaPreviewException SubtitleOffsetInvalid() => new(
        "subtitle_offset_invalid",
        "The subtitle offset produces an invalid cue time.");

    public static MediaPreviewException VideoFormatUnsupported() => new(
        "video_format_unsupported",
        "Only MP4, MKV, and AVI videos can be previewed.");

    public static MediaPreviewException VideoInvalid() => new(
        "video_invalid",
        "The selected entry is not a supported video file.");

    public static MediaPreviewException SymbolicLinkRejected() => new(
        "symbolic_link_rejected",
        "Symbolic links cannot be used for media previews.");

    public static MediaPreviewException SubtitleSelectionInvalid() => new(
        "subtitle_selection_invalid",
        "Select an SRT subtitle from the video's directory.");

    public static MediaPreviewException SessionNotFound() => new(
        "preview_session_not_found",
        "The media preview session was not found.");

    public static MediaPreviewException SessionExpired() => new(
        "preview_session_expired",
        "The media preview session has expired.");

    public static MediaPreviewException SessionStale() => new(
        "preview_session_stale",
        "The media file changed after the preview was opened.");

    public static MediaPreviewException SessionNotReady() => new(
        "preview_not_ready",
        "The media preview is not ready yet.");

    public static MediaPreviewException PreviewCapacityReached() => new(
        "preview_capacity_reached",
        "The media preview queue is full. Try again after another preview finishes.");

    public static MediaPreviewException MediaToolsUnavailable() => new(
        "media_tools_unavailable",
        "The media preview tools are unavailable.");

    public static MediaPreviewException MediaProbeFailed() => new(
        "media_probe_failed",
        "The video could not be inspected safely.");

    public static MediaPreviewException MediaTranscodeFailed() => new(
        "media_transcode_failed",
        "A browser-compatible preview could not be created.");

    public static MediaPreviewException HlsAssetInvalid() => new(
        "hls_asset_invalid",
        "The requested preview asset is invalid.");

    public static MediaPreviewException SubtitleMissing() => new(
        "subtitle_missing",
        "Select an SRT subtitle before saving a correction.");

    public static MediaPreviewException SubtitleSourceReadOnly() => new(
        "subtitle_source_read_only",
        "The selected source is read-only, so its subtitle cannot be changed.");

    public static MediaPreviewException SubtitleSavePlanNotFound() => new(
        "subtitle_save_plan_not_found",
        "The subtitle save plan was not found.");

    public static MediaPreviewException SubtitleSavePlanExpired() => new(
        "subtitle_save_plan_expired",
        "The subtitle save plan has expired. Review the change again.");

    public static MediaPreviewException SubtitleSavePlanStale() => new(
        "subtitle_save_plan_stale",
        "The subtitle changed after the save was reviewed.");

    public static MediaPreviewException SubtitleBackupUnavailable() => new(
        "subtitle_backup_unavailable",
        "A free backup filename could not be reserved.");

    public static MediaPreviewException SubtitleSaveFailed() => new(
        "subtitle_save_failed",
        "The corrected subtitle could not be saved; the original was preserved.");

    public static MediaPreviewException SubtitleRecoveryRequired() => new(
        "subtitle_recovery_required",
        "The corrected subtitle could not be published and the original is in its backup file. Manual recovery is required.");
}
