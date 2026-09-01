using System.Text;
using System.Text.Json;
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
    public async Task CreateAsync_queues_HLS_for_a_non_direct_container()
    {
        WriteVideo("Movies/movie.mkv");
        var service = CreateService(
            readOnly: false,
            new MediaProbeResult("matroska,webm", "h264", "aac", 5_000));

        var session = await service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mkv"),
            default);

        Assert.Equal(MediaPlaybackMode.Hls, session.PlaybackMode);
        Assert.Equal(MediaPreviewPhase.Transcoding, session.Phase);
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
        MediaProbeResult? probeResult = null)
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
        var options = Options.Create(new MediaPreviewOptions());
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
            NullLogger<MediaPreviewService>.Instance);
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
