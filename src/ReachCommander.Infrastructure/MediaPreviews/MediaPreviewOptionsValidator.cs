using Microsoft.Extensions.Options;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class MediaPreviewOptionsValidator : IValidateOptions<MediaPreviewOptions>
{
    private static readonly HashSet<string> ApprovedTranscodePresets =
        new(StringComparer.Ordinal) { "ultrafast", "superfast", "veryfast" };

    public ValidateOptionsResult Validate(string? name, MediaPreviewOptions options)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.FfprobePath))
        {
            failures.Add("MediaPreview:FfprobePath is required.");
        }

        if (string.IsNullOrWhiteSpace(options.FfmpegPath))
        {
            failures.Add("MediaPreview:FfmpegPath is required.");
        }

        if (options.MaximumTranscodeThreads is < 1 or > 8)
        {
            failures.Add("MediaPreview:MaximumTranscodeThreads must be between 1 and 8.");
        }

        if (!ApprovedTranscodePresets.Contains(options.TranscodePreset))
        {
            failures.Add(
                "MediaPreview:TranscodePreset must be ultrafast, superfast, or veryfast.");
        }

        if (options.QueueCapacity is < 1 or > 64)
        {
            failures.Add("MediaPreview:QueueCapacity must be between 1 and 64.");
        }

        if (options.MaximumProcessOutputCharacters is < 1024 or > 1024 * 1024)
        {
            failures.Add("MediaPreview:MaximumProcessOutputCharacters must be between 1 KiB and 1 MiB.");
        }

        if (options.MaximumSubtitleBytes is < 1024 or > 16L * 1024 * 1024)
        {
            failures.Add("MediaPreview:MaximumSubtitleBytes must be between 1 KiB and 16 MiB.");
        }

        if (options.MaximumSubtitleCues is < 1 or > 100_000)
        {
            failures.Add("MediaPreview:MaximumSubtitleCues must be between 1 and 100000.");
        }

        if (options.MaximumTemporaryOutputBytes <= 0 ||
            options.MaximumTranscodeDuration <= TimeSpan.Zero ||
            options.SessionInactivity <= TimeSpan.Zero ||
            options.PendingSessionInactivity <= TimeSpan.Zero ||
            options.CleanupInterval <= TimeSpan.Zero ||
            options.SavePlanLifetime <= TimeSpan.Zero ||
            options.MaximumOffsetMilliseconds <= 0)
        {
            failures.Add("Media preview size and duration limits must be positive.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
