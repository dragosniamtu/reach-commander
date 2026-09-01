using System.Text;
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

public sealed class SubtitleSaveTests : IDisposable
{
    private readonly Fixture _fixture = new();

    [Fact]
    public async Task Execute_preserves_original_and_publishes_corrected_name()
    {
        var session = await _fixture.OpenAsync();
        var plan = await _fixture.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            1_400,
            default);

        var result = await _fixture.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default);

        Assert.Equal(_fixture.OriginalBytes, File.ReadAllBytes(_fixture.BackupPath));
        Assert.Contains("00:00:02,400", File.ReadAllText(_fixture.SubtitlePath));
        Assert.Equal("/Movies/movie_original.srt", result.BackupPath);
        Assert.False(result.RecoveryRequired);
    }

    [Fact]
    public async Task Plan_uses_the_first_case_insensitively_free_backup_name()
    {
        File.WriteAllText(_fixture.BackupPath, "occupied");
        File.WriteAllText(
            Path.Combine(_fixture.MoviesPath, "MOVIE_ORIGINAL (2).SRT"),
            "occupied");
        var session = await _fixture.OpenAsync();

        var plan = await _fixture.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            -250,
            default);

        Assert.Equal("/Movies/movie_original (3).srt", plan.BackupPath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(600_001)]
    [InlineData(-600_001)]
    public async Task Plan_rejects_a_zero_or_out_of_range_offset(long offset)
    {
        var session = await _fixture.OpenAsync();

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            _fixture.Service.PlanSubtitleSaveAsync(
                session.SessionId,
                offset,
                default).AsTask());

        Assert.Equal("subtitle_offset_invalid", error.Code);
    }

    [Fact]
    public async Task Plan_rejects_a_read_only_source()
    {
        using var readOnly = new Fixture(readOnly: true);
        var session = await readOnly.OpenAsync();

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            readOnly.Service.PlanSubtitleSaveAsync(
                session.SessionId,
                500,
                default).AsTask());

        Assert.Equal("subtitle_source_read_only", error.Code);
    }

    [Fact]
    public async Task Execute_rejects_an_expired_plan()
    {
        var session = await _fixture.OpenAsync();
        var plan = await _fixture.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            500,
            default);
        _fixture.Clock.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            _fixture.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default).AsTask());

        Assert.Equal("subtitle_save_plan_expired", error.Code);
    }

    [Fact]
    public async Task Execute_rejects_a_subtitle_that_changed_after_planning()
    {
        var session = await _fixture.OpenAsync();
        var plan = await _fixture.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            500,
            default);
        await File.AppendAllTextAsync(_fixture.SubtitlePath, "changed");

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            _fixture.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default).AsTask());

        Assert.Equal("subtitle_save_plan_stale", error.Code);
        Assert.False(File.Exists(_fixture.BackupPath));
    }

    [Fact]
    public async Task Execute_rolls_the_original_back_when_publication_fails()
    {
        using var faulted = new Fixture(moveFailures: [2]);
        var session = await faulted.OpenAsync();
        var plan = await faulted.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            500,
            default);

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            faulted.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default).AsTask());

        Assert.Equal("subtitle_save_failed", error.Code);
        Assert.Equal(faulted.OriginalBytes, File.ReadAllBytes(faulted.SubtitlePath));
        Assert.False(File.Exists(faulted.BackupPath));
    }

    [Fact]
    public async Task Execute_preserves_the_original_when_the_backup_move_fails()
    {
        using var faulted = new Fixture(moveFailures: [1]);
        var session = await faulted.OpenAsync();
        var plan = await faulted.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            500,
            default);

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            faulted.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default).AsTask());

        Assert.Equal("subtitle_save_failed", error.Code);
        Assert.Equal(faulted.OriginalBytes, File.ReadAllBytes(faulted.SubtitlePath));
        Assert.False(File.Exists(faulted.BackupPath));
        Assert.Empty(Directory.EnumerateFiles(
            faulted.MoviesPath,
            ".reachcommander-subtitle-*.partial"));
    }

    [Fact]
    public async Task Execute_reports_recovery_required_when_rollback_fails()
    {
        using var faulted = new Fixture(moveFailures: [2, 3]);
        var session = await faulted.OpenAsync();
        var plan = await faulted.Service.PlanSubtitleSaveAsync(
            session.SessionId,
            500,
            default);

        var error = await Assert.ThrowsAsync<MediaPreviewException>(() =>
            faulted.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default).AsTask());

        Assert.Equal("subtitle_recovery_required", error.Code);
        Assert.True(File.Exists(faulted.BackupPath));
        Assert.False(File.Exists(faulted.SubtitlePath));
    }

    public void Dispose() => _fixture.Dispose();

    private sealed class Fixture : IDisposable
    {
        private readonly TemporaryDirectory _temporary = new();

        public Fixture(bool readOnly = false, int[]? moveFailures = null)
        {
            SourceRoot = _temporary.CreateDirectory("media");
            MoviesPath = _temporary.CreateDirectory("media/Movies");
            var videoPath = Path.Combine(MoviesPath, "movie.mp4");
            File.WriteAllBytes(videoPath, [0, 1, 2, 3]);
            SubtitlePath = Path.Combine(MoviesPath, "movie.srt");
            OriginalBytes = Encoding.UTF8.GetBytes(
                "1\r\n00:00:01,000 --> 00:00:02,000\r\nHello\r\n");
            File.WriteAllBytes(SubtitlePath, OriginalBytes);
            BackupPath = Path.Combine(MoviesPath, "movie_original.srt");

            var source = new SourceDefinition(
                "media",
                "Media",
                SourceRoot,
                IsReadOnly: readOnly,
                DefaultLeft: true,
                DefaultRight: false);
            var pathSecurity = new PathSecurityService(new FakeSourceCatalog(source));
            var options = Options.Create(new MediaPreviewOptions());
            var sessions = new MediaPreviewSessionStore(Clock, options);
            var plans = new SubtitleSavePlanStore(Clock, options);
            var localFileSystem = new LocalMediaPreviewFileSystem();
            IMediaPreviewFileSystem fileSystem = moveFailures is null
                ? localFileSystem
                : new FaultingFileSystem(localFileSystem, moveFailures);
            var planner = new SubtitleSavePlanner(
                sessions,
                pathSecurity,
                fileSystem,
                plans,
                Clock,
                options);
            var executor = new SubtitleSaveExecutor(
                sessions,
                pathSecurity,
                fileSystem,
                plans,
                new DirectoryMutationLock(),
                options);
            var dataPaths = AuthenticationDataPaths.ForRoot(
                _temporary.CreateDirectory("data"));
            Service = new MediaPreviewService(
                pathSecurity,
                new StubProbeRunner(),
                sessions,
                new MediaPreviewQueue(options),
                dataPaths,
                Clock,
                options,
                planner,
                executor,
                NullLogger<MediaPreviewService>.Instance);
        }

        public string SourceRoot { get; }

        public string MoviesPath { get; }

        public string SubtitlePath { get; }

        public string BackupPath { get; }

        public byte[] OriginalBytes { get; }

        public ManualTimeProvider Clock { get; } = new(
            new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

        public MediaPreviewService Service { get; }

        public ValueTask<MediaPreviewSession> OpenAsync() => Service.CreateAsync(
            new CreateMediaPreviewCommand("media", "/Movies/movie.mp4"),
            default);

        public void Dispose() => _temporary.Dispose();
    }

    private sealed class FaultingFileSystem(
        IMediaPreviewFileSystem inner,
        IReadOnlyCollection<int> moveFailures) : IMediaPreviewFileSystem
    {
        private int _moveCount;

        public MediaPreviewFileSnapshot GetFileSnapshot(string physicalPath) =>
            inner.GetFileSnapshot(physicalPath);

        public IReadOnlyList<string> ListNames(string directoryPhysicalPath) =>
            inner.ListNames(directoryPhysicalPath);

        public Task WriteNewAsync(
            string physicalPath,
            ReadOnlyMemory<byte> contents,
            CancellationToken cancellationToken) =>
            inner.WriteNewAsync(physicalPath, contents, cancellationToken);

        public void MoveFile(string sourcePhysicalPath, string destinationPhysicalPath)
        {
            _moveCount++;
            if (moveFailures.Contains(_moveCount))
            {
                throw new IOException("Injected move failure.");
            }

            inner.MoveFile(sourcePhysicalPath, destinationPhysicalPath);
        }

        public bool FileExists(string physicalPath) => inner.FileExists(physicalPath);

        public void DeleteFile(string physicalPath) => inner.DeleteFile(physicalPath);

        public void FlushDirectory(string directoryPhysicalPath) =>
            inner.FlushDirectory(directoryPhysicalPath);
    }

    private sealed class StubProbeRunner : IMediaProbeRunner
    {
        public ValueTask<MediaProbeResult> ProbeAsync(
            string inputPhysicalPath,
            CancellationToken cancellationToken) => ValueTask.FromResult(
            new MediaProbeResult(
                "mov,mp4,m4a,3gp,3g2,mj2",
                "h264",
                "aac",
                10_000));
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

    internal sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}
