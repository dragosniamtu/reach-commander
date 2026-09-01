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
}
