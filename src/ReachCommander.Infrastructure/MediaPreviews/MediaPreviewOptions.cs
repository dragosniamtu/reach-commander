namespace ReachCommander.Infrastructure.MediaPreviews;

public sealed class MediaPreviewOptions
{
    public const string SectionName = "MediaPreview";

    public bool Enabled { get; set; } = true;

    public string FfprobePath { get; set; } = "ffprobe";

    public string FfmpegPath { get; set; } = "ffmpeg";

    public int MaximumTranscodeThreads { get; set; } = 2;

    public string TranscodePreset { get; set; } = "ultrafast";

    public int QueueCapacity { get; set; } = 8;

    public int MaximumProcessOutputCharacters { get; set; } = 64 * 1024;

    public long MaximumSubtitleBytes { get; set; } = 4L * 1024 * 1024;

    public int MaximumSubtitleCues { get; set; } = 20_000;

    public long MaximumTemporaryOutputBytes { get; set; } = 8L * 1024 * 1024 * 1024;

    public TimeSpan MaximumTranscodeDuration { get; set; } = TimeSpan.FromMinutes(90);

    public TimeSpan SessionInactivity { get; set; } = TimeSpan.FromMinutes(20);

    public TimeSpan PendingSessionInactivity { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromSeconds(10);

    public long MaximumOffsetMilliseconds { get; set; } = 600_000;

    public TimeSpan SavePlanLifetime { get; set; } = TimeSpan.FromMinutes(10);
}
