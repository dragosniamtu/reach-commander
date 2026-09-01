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
}
