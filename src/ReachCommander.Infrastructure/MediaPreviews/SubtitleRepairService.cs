using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed class SubtitleRepairService(
    MediaPreviewSessionStore sessions, IPathSecurityService pathSecurity,
    ISubtitleSyncEngine engine, IOptions<MediaPreviewOptions> options)
    : BackgroundService, ISubtitleRepairService
{
    private static readonly SubtitleRepairStatus Idle = new("idle", "Ready to repair timing");
    private readonly ConcurrentDictionary<Guid, Work> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _initialized;
    private int _disposed;
    public bool HasActiveOperations => _jobs.Values.Any(job => !job.Completion.IsCompleted);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await engine.RecoverAsync(cancellationToken); _initialized = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaPreviewException) { }
        sessions.SubtitleReplaced += CancelReplacedSubtitle;
        await base.StartAsync(cancellationToken);
    }

    public async Task<SubtitleRepairCapabilities> GetCapabilitiesAsync(CancellationToken token) =>
        _initialized ? await engine.GetCapabilitiesAsync(token) : new(true, false, "subtitle_repair_unavailable",
            "Subtitle repair could not recover its temporary work. Check the local worker.");

    public async Task<SubtitleRepairStatus> StartAsync(Guid sessionId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var capabilities = await GetCapabilitiesAsync(token);
            if (!capabilities.Available) throw new MediaPreviewException(capabilities.ReasonCode!, capabilities.Detail!);
            if (_jobs.Values.Any(job => !job.Completion.IsCompleted))
                throw new MediaPreviewException("subtitle_repair_busy", "Another subtitle repair is running. Wait or cancel it first.");
            var session = sessions.GetRequired(sessionId);
            var subtitle = session.Subtitle ?? throw MediaPreviewException.SubtitleMissing();
            if (subtitle.Encoding?.RequiresReview == true)
                throw new MediaPreviewException("subtitle_encoding_review_required", "Review the decoded subtitle text before continuing.");
            await ValidateAsync(session, subtitle, token);
            if (_jobs.TryRemove(sessionId, out var previous)) previous.DisposeCancellation();
            var work = new Work(subtitle, CancellationTokenSource.CreateLinkedTokenSource(session.Lifetime.Token, _shutdown.Token));
            _jobs[sessionId] = work;
            work.Completion = RunAsync(session, work);
            if (sessions.TryGet(sessionId, out var current) && ReferenceEquals(current.Subtitle, subtitle))
                return work.Status;
            work.CancelIfRunning();
            return Idle;
        }
        finally { _gate.Release(); }
    }

    public ValueTask<SubtitleRepairStatus> GetAsync(Guid sessionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var session = sessions.GetRequired(sessionId);
        return ValueTask.FromResult(_jobs.TryGetValue(sessionId, out var work) && ReferenceEquals(session.Subtitle, work.Subtitle)
            ? work.Status : Idle);
    }

    public async Task<SubtitleRepairStatus> CancelAsync(Guid sessionId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var session = sessions.GetRequired(sessionId);
            if (!_jobs.TryGetValue(sessionId, out var work) || !ReferenceEquals(session.Subtitle, work.Subtitle)) return Idle;
            if (!work.Completion.IsCompleted)
            {
                work.Status = new("cancelling", "Stopping timing correction", work.Status.Percent);
                await work.Cancellation.CancelAsync();
                await work.Completion.WaitAsync(token);
            }
            return sessions.TryGet(sessionId, out var current) && ReferenceEquals(current.Subtitle, work.Subtitle)
                ? work.Status : Idle;
        }
        finally { _gate.Release(); }
    }

    private void CancelReplacedSubtitle(Guid sessionId, StoredSubtitle previous)
    {
        if (_jobs.TryGetValue(sessionId, out var work) && ReferenceEquals(previous, work.Subtitle))
            work.CancelIfRunning();
    }

    private async Task RunAsync(StoredMediaPreviewSession session, Work work)
    {
        var subtitle = work.Subtitle;
        try
        {
            var result = await engine.SynchronizeAsync(session.VideoPhysicalPath, subtitle.Document.TimingReference(), status =>
            {
                if (!work.Cancellation.IsCancellationRequested) work.Status = status;
                return Task.CompletedTask;
            }, work.Cancellation.Token);
            work.Cancellation.Token.ThrowIfCancellationRequested();
            var current = sessions.GetRequired(session.SessionId);
            if (!ReferenceEquals(current.Subtitle, subtitle)) throw MediaPreviewException.SubtitleSavePlanStale();
            await ValidateAsync(current, subtitle, work.Cancellation.Token);
            if (Math.Abs((double)result.OffsetMilliseconds) > options.Value.MaximumOffsetMilliseconds)
                throw MediaPreviewException.SubtitleOffsetInvalid();
            _ = subtitle.Document.RenderWithTiming(result.OffsetMilliseconds, result.TimeScale);
            work.Status = new("completed", "Timing correction ready to preview", 100, result.OffsetMilliseconds, result.TimeScale);
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
        { work.Status = new("cancelled", "Timing correction cancelled"); }
        catch (MediaPreviewException ex)
        { work.Status = new("failed", "Timing correction failed", ErrorCode: ex.Code); }
        catch (Exception)
        { work.Status = new("failed", "Timing correction failed", ErrorCode: "subtitle_repair_failed"); }
    }

    private async Task ValidateAsync(StoredMediaPreviewSession session, StoredSubtitle subtitle, CancellationToken token)
    {
        var video = await pathSecurity.ResolveAsync(session.SourceId, session.VideoLogicalPath, token);
        var srt = await pathSecurity.ResolveAsync(session.SourceId, subtitle.LogicalPath, token);
        MediaPreviewService.EnsureNoSymbolicLinks(video);
        MediaPreviewService.EnsureNoSymbolicLinks(srt);
        if (!SubtitleSavePlanner.PathsEqual(video.PhysicalPath, session.VideoPhysicalPath) ||
            MediaPreviewService.CaptureFingerprint(video.PhysicalPath) != session.VideoFingerprint)
            throw MediaPreviewException.SessionStale();
        if (!SubtitleSavePlanner.PathsEqual(srt.PhysicalPath, subtitle.PhysicalPath) ||
            MediaPreviewService.CaptureFingerprint(srt.PhysicalPath) != subtitle.Fingerprint ||
            (subtitle.ContentHash is not null && subtitle.ContentHash != Convert.ToHexString(SHA256.HashData(
                await MediaPreviewService.ReadBoundedSubtitleAsync(srt.PhysicalPath, options.Value.MaximumSubtitleBytes, token)))))
            throw MediaPreviewException.SubtitleSavePlanStale();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    foreach (var (id, work) in _jobs)
                    {
                        if (sessions.TryGet(id, out var current) && ReferenceEquals(current.Subtitle, work.Subtitle)) continue;
                        await work.Cancellation.CancelAsync();
                        if (work.Completion.IsCompleted && _jobs.TryRemove(id, out _)) work.DisposeCancellation();
                    }
                    if (_jobs.Values.All(job => job.Completion.IsCompleted))
                    {
                        await engine.RecoverAsync(stoppingToken);
                        _initialized = true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MediaPreviewException) { }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Task cancellation;
        // The host may stop a service after (or concurrently with) disposal.
        // Serialize access to the source while still awaiting active repairs below.
        lock (_shutdown)
            cancellation = _disposed == 0 ? _shutdown.CancelAsync() : Task.CompletedTask;
        await cancellation;
        await Task.WhenAll(_jobs.Values.Select(job => job.Completion)).WaitAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_shutdown)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            sessions.SubtitleReplaced -= CancelReplacedSubtitle;
            _shutdown.Cancel();
            base.Dispose();
            _shutdown.Dispose();
        }
    }

    private sealed class Work(StoredSubtitle subtitle, CancellationTokenSource cancellation)
    {
        // Each selection and save creates a new snapshot, even if the bytes are identical.
        public StoredSubtitle Subtitle { get; } = subtitle;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public volatile SubtitleRepairStatus Status = new("queued", "Waiting for subtitle worker");
        public volatile Task Completion = Task.CompletedTask;

        public void CancelIfRunning()
        {
            lock (Cancellation)
            {
                if (!Completion.IsCompleted) Cancellation.Cancel();
            }
        }

        public void DisposeCancellation()
        {
            lock (Cancellation) Cancellation.Dispose();
        }
    }
}
