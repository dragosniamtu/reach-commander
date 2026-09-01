using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Application.Sources;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.Infrastructure.MediaPreviews;
using ReachCommander.Infrastructure.Mutations;
using ReachCommander.Infrastructure.Security;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.MediaPreviews;

public sealed class MediaPreviewServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _temporary = new();
    private readonly string _sourceRoot;
    private readonly ManualTimeProvider _clock = new(Now);

    public MediaPreviewServiceTests()
    {
        _sourceRoot = _temporary.CreateDirectory("media");
    }

    [Fact]
    public async Task CreateAsync_auto_selects_same_name_srt_without_exposing_physical_paths()
    {
        WriteVideo("Movies/Family Movie.mp4");
        WriteSubtitle("Movies/Family Movie.srt", "Hello");
        var service = CreateService(readOnly: false);

        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/Family Movie.mp4"),
            default);

        Assert.Equal("/Movies/Family Movie.srt", session.SubtitlePath);
        Assert.Single(session.Cues);
        Assert.Equal("Hello", session.Cues[0].Text);
        Assert.DoesNotContain(
            _sourceRoot,
            JsonSerializer.Serialize(session),
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("movie.mov")]
    [InlineData("movie.webm")]
    [InlineData("movie.srt")]
    public async Task CreateAsync_rejects_video_extensions_outside_the_allowlist(string name)
    {
        WriteVideo($"Movies/{name}");
        var service = CreateService(readOnly: false);

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.CreateAsync(
                new CreateMediaPreviewCommand("media", $"/Movies/{name}"),
                default).AsTask());

        Assert.Equal("video_format_unsupported", error.Code);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_directory()
    {
        Directory.CreateDirectory(Path.Combine(_sourceRoot, "Movies", "folder.mp4"));
        var service = CreateService(readOnly: false);

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.CreateAsync(
                new CreateMediaPreviewCommand("media", "/Movies/folder.mp4"),
                default).AsTask());

        Assert.Equal("video_invalid", error.Code);
    }

    [Fact]
    public async Task CreateAsync_projects_source_read_only_state()
    {
        WriteVideo("Movies/movie.mp4");
        var service = CreateService(readOnly: true);

        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mp4"),
            default);

        Assert.True(session.SourceReadOnly);
    }

    [Fact]
    public async Task SelectSubtitleAsync_accepts_only_an_SRT_in_the_video_directory()
    {
        WriteVideo("Movies/movie.mp4");
        WriteSubtitle("Movies/alternate.srt", "Alternate");
        WriteSubtitle("Other/outside.srt", "Outside");
        var service = CreateService(readOnly: false);
        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mp4"),
            default);

        var selected = await service.SelectSubtitleAsync(
            session.SessionId,
            "/Movies/alternate.srt",
            default);
        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.SelectSubtitleAsync(
                session.SessionId,
                "/Other/outside.srt",
                default).AsTask());

        Assert.Equal("/Movies/alternate.srt", selected.SubtitlePath);
        Assert.Equal("Alternate", selected.Cues[0].Text);
        Assert.Equal("subtitle_selection_invalid", error.Code);
    }

    [Fact]
    public async Task GetAsync_expires_an_inactive_session()
    {
        WriteVideo("Movies/movie.mp4");
        var service = CreateService(readOnly: false);
        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mp4"),
            default);
        _clock.Advance(TimeSpan.FromMinutes(21));

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.GetAsync(session.SessionId, default).AsTask());

        Assert.Equal("preview_session_expired", error.Code);
    }

    [Fact]
    public async Task CreateAsync_reports_HLS_as_queued_until_the_worker_starts()
    {
        WriteVideo("Movies/movie.mkv");
        var service = CreateService(
            readOnly: false,
            new MediaProbeResult("matroska,webm", "h264", "aac", 5_000));

        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mkv"),
            default);

        Assert.Equal(MediaPlaybackMode.Hls, session.PlaybackMode);
        Assert.Equal(MediaPreviewPhase.Queued, session.Phase);
    }

    [Fact]
    public async Task ProcessQueuedAsync_reports_transcoding_only_while_the_runner_is_active()
    {
        WriteVideo("Movies/movie.mkv");
        var service = CreateService(
            readOnly: false,
            new MediaProbeResult("matroska,webm", "hevc", "aac", 5_000));
        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mkv"),
            default);
        var runner = new BlockingTranscodeRunner();

        var processing = service.ProcessQueuedAsync(session.SessionId, runner, default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var active = await service.GetAsync(session.SessionId, default);
        Assert.Equal(MediaPreviewPhase.Transcoding, active.Phase);
        Assert.True(active.TranscodeActive);

        runner.Complete();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        var completed = await service.GetAsync(session.SessionId, default);
        Assert.Equal(MediaPreviewPhase.Ready, completed.Phase);
        Assert.False(completed.TranscodeActive);
    }

    [Fact]
    public async Task DeleteAbandonedPendingOutputs_cancels_an_active_transcode_without_a_browser_heartbeat()
    {
        WriteVideo("Movies/abandoned.mkv");
        var service = CreateService(
            readOnly: false,
            new MediaProbeResult("matroska,webm", "hevc", "aac", 5_000),
            mediaOptions: new MediaPreviewOptions
            {
                PendingSessionInactivity = TimeSpan.FromSeconds(30),
            });
        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/abandoned.mkv"),
            default);
        var runner = new BlockingTranscodeRunner(announceReadyOnStart: true);
        var processing = service.ProcessQueuedAsync(session.SessionId, runner, default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var playable = await service.GetAsync(session.SessionId, default);
        Assert.Equal(MediaPreviewPhase.Ready, playable.Phase);
        Assert.True(playable.TranscodeActive);
        _clock.Advance(TimeSpan.FromSeconds(31));

        service.DeleteAbandonedPendingOutputs();

        await processing.WaitAsync(TimeSpan.FromSeconds(2));
        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.GetAsync(session.SessionId, default).AsTask());
        Assert.Equal("preview_session_not_found", error.Code);
    }

    [Fact]
    public async Task Hls_lifecycle_emits_traceable_session_logs()
    {
        WriteVideo("Movies/diagnostic.mkv");
        var logger = new RecordingLogger<MediaPreviewService>();
        var service = CreateService(
            readOnly: false,
            new MediaProbeResult("matroska,webm", "hevc", "aac", 5_000),
            logger);
        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/diagnostic.mkv"),
            default);
        var runner = new BlockingTranscodeRunner();

        var processing = service.ProcessQueuedAsync(session.SessionId, runner, default);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        runner.Complete();
        await processing.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(logger.Messages, message =>
            message.Contains(session.SessionId.ToString(), StringComparison.Ordinal) &&
            message.Contains("queued", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, message =>
            message.Contains(session.SessionId.ToString(), StringComparison.Ordinal) &&
            message.Contains("started transcoding", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(logger.Messages, message =>
            message.Contains(session.SessionId.ToString(), StringComparison.Ordinal) &&
            message.Contains("ready", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetAsync_rejects_a_video_that_changed_after_the_session_was_created()
    {
        WriteVideo("Movies/movie.mp4");
        var service = CreateService(readOnly: false);
        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mp4"),
            default);
        await using (var stream = new FileStream(
                         Path.Combine(_sourceRoot, "Movies", "movie.mp4"),
                         FileMode.Append,
                         FileAccess.Write,
                         FileShare.None))
        {
            await stream.WriteAsync(new byte[] { 4 });
        }

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.GetAsync(session.SessionId, default).AsTask());

        Assert.Equal("preview_session_stale", error.Code);
    }

    [Fact]
    public async Task CreateAsync_rejects_a_symbolic_link()
    {
        WriteVideo("Movies/target.mp4");
        var link = Path.Combine(_sourceRoot, "Movies", "linked.mp4");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine(_sourceRoot, "Movies", "target.mp4"));
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        var service = CreateService(readOnly: false);
        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            service.CreateAsync(
                new CreateMediaPreviewCommand("media", "/Movies/linked.mp4"),
                default).AsTask());

        Assert.Equal("symbolic_link_rejected", error.Code);
    }

    public void Dispose() => _temporary.Dispose();

    private MediaPreviewService CreateService(
        bool readOnly,
        MediaProbeResult? probeResult = null,
        ILogger<MediaPreviewService>? serviceLogger = null,
        MediaPreviewOptions? mediaOptions = null)
    {
        var source = new SourceDefinition(
            "media",
            "Media",
            _sourceRoot,
            IsReadOnly: readOnly,
            DefaultLeft: true,
            DefaultRight: false);
        var pathSecurity = new PathSecurityService(new FakeSourceCatalog(source));
        var paths = AuthenticationDataPaths.ForRoot(_temporary.CreateDirectory("data"));
        var options = Options.Create(mediaOptions ?? new MediaPreviewOptions());
        var store = new MediaPreviewSessionStore(_clock, options);
        var queue = new MediaPreviewQueue(options);
        var fileSystem = new LocalMediaPreviewFileSystem();
        var plans = new SubtitleSavePlanStore(_clock, options);
        var planner = new SubtitleSavePlanner(
            store,
            pathSecurity,
            fileSystem,
            plans,
            _clock,
            options);
        var executor = new SubtitleSaveExecutor(
            store,
            pathSecurity,
            fileSystem,
            plans,
            new DirectoryMutationLock(),
            options);
        return new MediaPreviewService(
            pathSecurity,
            new StubProbeRunner(probeResult ?? new MediaProbeResult(
                "mov,mp4,m4a,3gp,3g2,mj2",
                "h264",
                "aac",
                10_000)),
            store,
            queue,
            paths,
            _clock,
            options,
            planner,
            executor,
            serviceLogger ?? NullLogger<MediaPreviewService>.Instance);
    }

    private void WriteVideo(string relativePath)
    {
        var physicalPath = Path.Combine(_sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        File.WriteAllBytes(physicalPath, [0, 1, 2, 3]);
    }

    private void WriteSubtitle(string relativePath, string text)
    {
        var physicalPath = Path.Combine(_sourceRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        File.WriteAllText(
            physicalPath,
            $"1\r\n00:00:00,000 --> 00:00:01,000\r\n{text}\r\n",
            new UTF8Encoding(false, true));
    }

    private sealed class StubProbeRunner(MediaProbeResult result) : IMediaProbeRunner
    {
        public ValueTask<MediaProbeResult> ProbeAsync(
            string inputPhysicalPath,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class BlockingTranscodeRunner(bool announceReadyOnStart = false)
        : IMediaTranscodeRunner
    {
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(
            string inputPhysicalPath,
            string outputDirectory,
            Action ready,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (announceReadyOnStart)
            {
                ready();
            }
            await _completion.Task.WaitAsync(cancellationToken);
            ready();
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class FakeSourceCatalog(SourceDefinition source) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([source]);

        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<SourceDefinition> GetRequiredAsync(
            string sourceId,
            CancellationToken cancellationToken) => ValueTask.FromResult(source);
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
