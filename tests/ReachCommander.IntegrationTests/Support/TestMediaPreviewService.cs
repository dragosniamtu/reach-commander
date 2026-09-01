using System.Text;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.IntegrationTests;

internal sealed class TestMediaPreviewService : IMediaPreviewService
{
    private static readonly DateTimeOffset ExpiresAt =
        DateTimeOffset.Parse("2026-09-01T10:20:00Z");
    private readonly Dictionary<Guid, MediaPreviewSession> _sessions = new();
    private readonly Dictionary<Guid, SubtitleSavePlan> _plans = new();

    public ValueTask<MediaPreviewSession> CreateAsync(
        CreateMediaPreviewCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var session = new MediaPreviewSession(
            id,
            MediaPreviewPhase.Ready,
            MediaPlaybackMode.Direct,
            Path.GetFileName(command.VideoPath),
            command.VideoPath,
            10_000,
            "/Movies/Family Movie.srt",
            [new SubtitleCue(0, 1_000, 2_000, "Hello")],
            SourceReadOnly: false,
            ExpiresAt);
        _sessions[id] = session;
        return ValueTask.FromResult(session);
    }

    public ValueTask<MediaPreviewSession> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(Get(sessionId));

    public ValueTask<MediaPreviewSession> RequestFallbackAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var updated = Get(sessionId) with
        {
            Phase = MediaPreviewPhase.Queued,
            PlaybackMode = MediaPlaybackMode.Hls,
        };
        _sessions[sessionId] = updated;
        return ValueTask.FromResult(updated);
    }

    public ValueTask<MediaPreviewSession> SelectSubtitleAsync(
        Guid sessionId,
        string subtitlePath,
        CancellationToken cancellationToken)
    {
        var updated = Get(sessionId) with
        {
            SubtitlePath = subtitlePath,
            Cues = [new SubtitleCue(0, 500, 1_500, "Alternate")],
        };
        _sessions[sessionId] = updated;
        return ValueTask.FromResult(updated);
    }

    public ValueTask<MediaAsset> OpenDirectContentAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        _ = Get(sessionId);
        var content = new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        return ValueTask.FromResult(new MediaAsset(
            new MemoryStream(content, writable: false),
            "video/mp4",
            content.Length,
            EnableRanges: true));
    }

    public ValueTask<MediaAsset> OpenHlsAssetAsync(
        Guid sessionId,
        string assetName,
        CancellationToken cancellationToken)
    {
        _ = Get(sessionId);
        if (assetName != "index.m3u8" &&
            !(assetName.StartsWith("segment-", StringComparison.Ordinal) &&
              assetName.EndsWith(".ts", StringComparison.Ordinal)))
        {
            throw MediaPreviewException.HlsAssetInvalid();
        }

        var bytes = Encoding.UTF8.GetBytes("#EXTM3U\n");
        return ValueTask.FromResult(new MediaAsset(
            new MemoryStream(bytes, writable: false),
            "application/vnd.apple.mpegurl",
            bytes.Length,
            EnableRanges: false));
    }

    public ValueTask CloseAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        _sessions.Remove(sessionId);
        return ValueTask.CompletedTask;
    }

    public ValueTask<SubtitleSavePlan> PlanSubtitleSaveAsync(
        Guid sessionId,
        long offsetMilliseconds,
        CancellationToken cancellationToken)
    {
        _ = Get(sessionId);
        var plan = new SubtitleSavePlan(
            Guid.NewGuid(),
            ExpiresAt,
            "/Movies/Family Movie.srt",
            "/Movies/Family Movie_original.srt",
            offsetMilliseconds,
            CanExecute: true);
        _plans[plan.PlanId] = plan;
        return ValueTask.FromResult(plan);
    }

    public ValueTask<SubtitleSaveResult> ExecuteSubtitleSaveAsync(
        Guid planId,
        CancellationToken cancellationToken)
    {
        if (!_plans.Remove(planId, out var plan))
        {
            throw MediaPreviewException.SubtitleSavePlanNotFound();
        }

        return ValueTask.FromResult(new SubtitleSaveResult(
            plan.SubtitlePath,
            plan.BackupPath,
            RecoveryRequired: false));
    }

    private MediaPreviewSession Get(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session
            : throw MediaPreviewException.SessionNotFound();
}
