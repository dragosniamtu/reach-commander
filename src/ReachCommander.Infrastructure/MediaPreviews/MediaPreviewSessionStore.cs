using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed record MediaFileFingerprint(
    long Length,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes);

internal sealed record StoredSubtitle(
    string LogicalPath,
    string PhysicalPath,
    MediaFileFingerprint Fingerprint,
    SrtDocument Document);

internal sealed record StoredMediaPreviewSession(
    Guid SessionId,
    string SourceId,
    string VideoLogicalPath,
    string VideoPhysicalPath,
    string VideoName,
    MediaFileFingerprint VideoFingerprint,
    MediaPreviewPhase Phase,
    MediaPlaybackMode PlaybackMode,
    long? DurationMilliseconds,
    StoredSubtitle? Subtitle,
    bool SourceReadOnly,
    string? OutputDirectory,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastAccessedAt,
    CancellationTokenSource Lifetime,
    string? FailureCode = null,
    string? FailureDetail = null);

internal sealed class MediaPreviewSessionStore(
    TimeProvider clock,
    IOptions<MediaPreviewOptions> options)
{
    private readonly ConcurrentDictionary<Guid, StoredMediaPreviewSession> _sessions = new();
    private readonly TimeSpan _inactivity = options.Value.SessionInactivity;

    public void Add(StoredMediaPreviewSession session)
    {
        if (!_sessions.TryAdd(session.SessionId, session))
        {
            throw new InvalidOperationException("A media preview session ID was reused.");
        }
    }

    public StoredMediaPreviewSession GetRequired(Guid sessionId, bool touch = true)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw MediaPreviewException.SessionNotFound();
        }

        var now = clock.GetUtcNow();
        if (now - session.LastAccessedAt >= _inactivity)
        {
            throw MediaPreviewException.SessionExpired();
        }

        if (!touch)
        {
            return session;
        }

        var touched = session with { LastAccessedAt = now };
        _sessions.TryUpdate(sessionId, touched, session);
        return touched;
    }

    public bool TryGet(Guid sessionId, out StoredMediaPreviewSession session)
    {
        try
        {
            session = GetRequired(sessionId, touch: false);
            return true;
        }
        catch (MediaPreviewException)
        {
            session = null!;
            return false;
        }
    }

    public StoredMediaPreviewSession Update(
        Guid sessionId,
        Func<StoredMediaPreviewSession, StoredMediaPreviewSession> update)
    {
        while (true)
        {
            var current = GetRequired(sessionId, touch: false);
            var next = update(current);
            if (_sessions.TryUpdate(sessionId, next, current))
            {
                return next;
            }
        }
    }

    public StoredMediaPreviewSession? Remove(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return null;
        }

        session.Lifetime.Cancel();
        return session;
    }

    public IReadOnlyList<StoredMediaPreviewSession> RemoveExpired()
    {
        var cutoff = clock.GetUtcNow() - _inactivity;
        var removed = new List<StoredMediaPreviewSession>();
        foreach (var pair in _sessions)
        {
            if (pair.Value.LastAccessedAt > cutoff ||
                !_sessions.TryRemove(pair.Key, out var session))
            {
                continue;
            }

            session.Lifetime.Cancel();
            removed.Add(session);
        }

        return removed;
    }

    public DateTimeOffset ExpiresAt(StoredMediaPreviewSession session) =>
        session.LastAccessedAt + _inactivity;
}
