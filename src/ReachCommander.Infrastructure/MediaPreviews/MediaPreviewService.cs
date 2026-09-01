using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Infrastructure.Authentication;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed partial class MediaPreviewService(
    IPathSecurityService pathSecurity,
    IMediaProbeRunner probeRunner,
    MediaPreviewSessionStore sessions,
    MediaPreviewQueue queue,
    AuthenticationDataPaths dataPaths,
    TimeProvider clock,
    IOptions<MediaPreviewOptions> options,
    ILogger<MediaPreviewService> logger) : IMediaPreviewService
{
    private static readonly HashSet<string> SupportedVideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".avi" };

    private readonly MediaPreviewOptions _options = options.Value;
    private readonly string _previewRoot = Path.Combine(dataPaths.RootPath, "media-previews");

    public async ValueTask<MediaPreviewSession> CreateAsync(
        CreateMediaPreviewCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_options.Enabled)
        {
            throw MediaPreviewException.MediaToolsUnavailable();
        }

        var video = await pathSecurity.ResolveAsync(
            command.SourceId,
            command.VideoPath,
            cancellationToken);
        EnsureAllowedVideo(video);
        EnsureNoSymbolicLinks(video);
        var videoFingerprint = CaptureFingerprint(video.PhysicalPath);
        var subtitle = await FindSameNameSubtitleAsync(video, cancellationToken);
        var probe = await probeRunner.ProbeAsync(video.PhysicalPath, cancellationToken);
        var now = clock.GetUtcNow();
        var sessionId = Guid.NewGuid();
        var playbackMode = probe.CanPlayDirectly &&
            Path.GetExtension(video.LogicalPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? MediaPlaybackMode.Direct
            : MediaPlaybackMode.Hls;
        var phase = playbackMode == MediaPlaybackMode.Direct
            ? MediaPreviewPhase.Ready
            : MediaPreviewPhase.Transcoding;
        var outputDirectory = playbackMode == MediaPlaybackMode.Hls
            ? GetSessionOutputDirectory(sessionId)
            : null;
        var stored = new StoredMediaPreviewSession(
            sessionId,
            video.Source.Id,
            video.LogicalPath,
            video.PhysicalPath,
            Path.GetFileName(video.PhysicalPath),
            videoFingerprint,
            phase,
            playbackMode,
            probe.DurationMilliseconds,
            subtitle,
            video.Source.IsReadOnly,
            outputDirectory,
            now,
            now,
            new CancellationTokenSource());
        sessions.Add(stored);

        if (playbackMode == MediaPlaybackMode.Hls && !queue.TryEnqueue(sessionId))
        {
            sessions.Remove(sessionId)?.Lifetime.Dispose();
            throw MediaPreviewException.PreviewCapacityReached();
        }

        return Map(stored);
    }

    public ValueTask<MediaPreviewSession> GetAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.GetRequired(sessionId);
        EnsureVideoIsCurrent(session);
        return ValueTask.FromResult(Map(session));
    }

    public ValueTask<MediaPreviewSession> RequestFallbackAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = sessions.GetRequired(sessionId);
        EnsureVideoIsCurrent(current);
        if (current.PlaybackMode == MediaPlaybackMode.Hls)
        {
            return ValueTask.FromResult(Map(current));
        }

        var outputDirectory = GetSessionOutputDirectory(sessionId);
        var updated = sessions.Update(
            sessionId,
            session => session with
            {
                PlaybackMode = MediaPlaybackMode.Hls,
                Phase = MediaPreviewPhase.Transcoding,
                OutputDirectory = outputDirectory,
                FailureCode = null,
                FailureDetail = null,
                LastAccessedAt = clock.GetUtcNow(),
            });
        if (!queue.TryEnqueue(sessionId))
        {
            sessions.Update(
                sessionId,
                session => session with
                {
                    PlaybackMode = MediaPlaybackMode.Direct,
                    Phase = MediaPreviewPhase.Ready,
                    OutputDirectory = null,
                });
            throw MediaPreviewException.PreviewCapacityReached();
        }

        return ValueTask.FromResult(Map(updated));
    }

    public async ValueTask<MediaPreviewSession> SelectSubtitleAsync(
        Guid sessionId,
        string subtitlePath,
        CancellationToken cancellationToken)
    {
        var current = sessions.GetRequired(sessionId);
        EnsureVideoIsCurrent(current);
        var requested = await pathSecurity.ResolveAsync(
            current.SourceId,
            subtitlePath,
            cancellationToken);
        if (!GetLogicalDirectory(requested.LogicalPath).Equals(
                GetLogicalDirectory(current.VideoLogicalPath),
                StringComparison.Ordinal) ||
            !Path.GetExtension(requested.LogicalPath).Equals(
                ".srt",
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(requested.PhysicalPath))
        {
            throw MediaPreviewException.SubtitleSelectionInvalid();
        }

        EnsureNoSymbolicLinks(requested);
        var subtitle = await ReadSubtitleAsync(requested, cancellationToken);
        var updated = sessions.Update(
            sessionId,
            session => session with
            {
                Subtitle = subtitle,
                LastAccessedAt = clock.GetUtcNow(),
            });
        return Map(updated);
    }

    public ValueTask<MediaAsset> OpenDirectContentAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.GetRequired(sessionId);
        EnsureVideoIsCurrent(session);
        if (session.Phase != MediaPreviewPhase.Ready ||
            session.PlaybackMode != MediaPlaybackMode.Direct)
        {
            throw MediaPreviewException.SessionNotReady();
        }

        Stream stream = new FileStream(
            session.VideoPhysicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(new MediaAsset(
            stream,
            "video/mp4",
            session.VideoFingerprint.Length,
            EnableRanges: true));
    }

    public ValueTask<MediaAsset> OpenHlsAssetAsync(
        Guid sessionId,
        string assetName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var session = sessions.GetRequired(sessionId);
        EnsureVideoIsCurrent(session);
        if (session.Phase != MediaPreviewPhase.Ready ||
            session.PlaybackMode != MediaPlaybackMode.Hls ||
            session.OutputDirectory is null)
        {
            throw MediaPreviewException.SessionNotReady();
        }

        if (assetName != "index.m3u8" && !HlsSegmentNamePattern().IsMatch(assetName))
        {
            throw MediaPreviewException.HlsAssetInvalid();
        }

        var physicalPath = Path.Combine(session.OutputDirectory, assetName);
        if (!File.Exists(physicalPath))
        {
            throw MediaPreviewException.SessionNotReady();
        }

        var file = new FileInfo(physicalPath);
        Stream stream = new FileStream(
            physicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(new MediaAsset(
            stream,
            assetName.EndsWith(".m3u8", StringComparison.Ordinal)
                ? "application/vnd.apple.mpegurl"
                : "video/mp2t",
            file.Length,
            EnableRanges: false));
    }

    public ValueTask CloseAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var removed = sessions.Remove(sessionId);
        if (removed is not null)
        {
            TryDeleteOutput(removed.OutputDirectory);
            removed.Lifetime.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal async Task ProcessQueuedAsync(
        Guid sessionId,
        IMediaTranscodeRunner transcodeRunner,
        CancellationToken stoppingToken)
    {
        if (!sessions.TryGet(sessionId, out var session) || session.OutputDirectory is null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken,
            session.Lifetime.Token);
        try
        {
            EnsureVideoIsCurrent(session);
            await transcodeRunner.RunAsync(
                session.VideoPhysicalPath,
                session.OutputDirectory,
                () => MarkReady(sessionId),
                linked.Token);
            MarkReady(sessionId);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            TryDeleteOutput(session.OutputDirectory);
        }
        catch (MediaPreviewException exception)
        {
            MarkFailed(sessionId, exception.Code, exception.PublicDetail);
            TryDeleteOutput(session.OutputDirectory);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unexpected media preview failure for session {SessionId}.",
                sessionId);
            var safe = MediaPreviewException.MediaTranscodeFailed();
            MarkFailed(sessionId, safe.Code, safe.PublicDetail);
            TryDeleteOutput(session.OutputDirectory);
        }
    }

    internal void DeleteExpiredOutputs()
    {
        foreach (var session in sessions.RemoveExpired())
        {
            TryDeleteOutput(session.OutputDirectory);
            session.Lifetime.Dispose();
        }
    }

    internal void DeleteRecoveredOutputs()
    {
        try
        {
            if (Directory.Exists(_previewRoot))
            {
                Directory.Delete(_previewRoot, recursive: true);
            }

            Directory.CreateDirectory(_previewRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "Temporary media preview recovery cleanup could not complete.");
        }
    }

    private async ValueTask<StoredSubtitle?> FindSameNameSubtitleAsync(
        ResolvedSourcePath video,
        CancellationToken cancellationToken)
    {
        var logicalDirectory = GetLogicalDirectory(video.LogicalPath);
        var subtitleName = $"{Path.GetFileNameWithoutExtension(video.LogicalPath)}.srt";
        var candidate = await pathSecurity.ResolveChildAsync(
            video.Source.Id,
            logicalDirectory,
            subtitleName,
            cancellationToken);
        if (!File.Exists(candidate.PhysicalPath))
        {
            return null;
        }

        EnsureNoSymbolicLinks(candidate);
        return await ReadSubtitleAsync(candidate, cancellationToken);
    }

    private async ValueTask<StoredSubtitle> ReadSubtitleAsync(
        ResolvedSourcePath subtitle,
        CancellationToken cancellationToken)
    {
        var fingerprint = CaptureFingerprint(subtitle.PhysicalPath);
        if (fingerprint.Length > _options.MaximumSubtitleBytes)
        {
            throw MediaPreviewException.SubtitleTooLarge();
        }

        var bytes = await File.ReadAllBytesAsync(subtitle.PhysicalPath, cancellationToken);
        var document = new SrtParser(
            checked((int)_options.MaximumSubtitleBytes),
            _options.MaximumSubtitleCues).Parse(bytes);
        return new StoredSubtitle(
            subtitle.LogicalPath,
            subtitle.PhysicalPath,
            fingerprint,
            document);
    }

    private static void EnsureAllowedVideo(ResolvedSourcePath video)
    {
        if (!SupportedVideoExtensions.Contains(Path.GetExtension(video.LogicalPath)))
        {
            throw MediaPreviewException.VideoFormatUnsupported();
        }

        if (!File.Exists(video.PhysicalPath) || Directory.Exists(video.PhysicalPath))
        {
            throw MediaPreviewException.VideoInvalid();
        }
    }

    private static void EnsureNoSymbolicLinks(ResolvedSourcePath resolved)
    {
        var current = Path.GetFullPath(resolved.Source.RootPath);
        EnsureEntryIsNotLink(current);
        foreach (var segment in resolved.LogicalPath.Split(
                     '/',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureEntryIsNotLink(current);
        }
    }

    private static void EnsureEntryIsNotLink(string physicalPath)
    {
        FileSystemInfo entry = Directory.Exists(physicalPath)
            ? new DirectoryInfo(physicalPath)
            : new FileInfo(physicalPath);
        if (entry.Exists &&
            (entry.LinkTarget is not null || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw MediaPreviewException.SymbolicLinkRejected();
        }
    }

    private static MediaFileFingerprint CaptureFingerprint(string physicalPath)
    {
        try
        {
            var file = new FileInfo(physicalPath);
            file.Refresh();
            if (!file.Exists)
            {
                throw MediaPreviewException.SessionStale();
            }

            return new MediaFileFingerprint(
                file.Length,
                new DateTimeOffset(file.LastWriteTimeUtc),
                file.Attributes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw MediaPreviewException.SessionStale();
        }
    }

    private static string GetLogicalDirectory(string logicalPath)
    {
        var separator = logicalPath.LastIndexOf('/');
        return separator <= 0 ? "/" : logicalPath[..separator];
    }

    private void EnsureVideoIsCurrent(StoredMediaPreviewSession session)
    {
        var current = CaptureFingerprint(session.VideoPhysicalPath);
        if (current != session.VideoFingerprint)
        {
            throw MediaPreviewException.SessionStale();
        }
    }

    private void MarkReady(Guid sessionId)
    {
        if (!sessions.TryGet(sessionId, out _))
        {
            return;
        }

        sessions.Update(
            sessionId,
            session => session with
            {
                Phase = MediaPreviewPhase.Ready,
                FailureCode = null,
                FailureDetail = null,
            });
    }

    private void MarkFailed(Guid sessionId, string code, string detail)
    {
        if (!sessions.TryGet(sessionId, out _))
        {
            return;
        }

        sessions.Update(
            sessionId,
            session => session with
            {
                Phase = MediaPreviewPhase.Failed,
                FailureCode = code,
                FailureDetail = detail,
            });
    }

    private MediaPreviewSession Map(StoredMediaPreviewSession session) => new(
        session.SessionId,
        session.Phase,
        session.PlaybackMode,
        session.VideoName,
        session.VideoLogicalPath,
        session.DurationMilliseconds,
        session.Subtitle?.LogicalPath,
        session.Subtitle?.Document.Cues ?? [],
        session.SourceReadOnly,
        sessions.ExpiresAt(session),
        session.FailureCode,
        session.FailureDetail);

    private string GetSessionOutputDirectory(Guid sessionId) =>
        Path.Combine(_previewRoot, sessionId.ToString("N"));

    private static void TryDeleteOutput(string? outputDirectory)
    {
        if (outputDirectory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^segment-[0-9]{6}\\.ts$", RegexOptions.CultureInvariant)]
    private static partial Regex HlsSegmentNamePattern();
}
