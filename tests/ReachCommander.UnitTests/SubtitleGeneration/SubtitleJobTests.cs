using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Domain.Sources;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.Infrastructure.Security;
using ReachCommander.Infrastructure.SubtitleGeneration;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SubtitleGeneration;

public sealed class SubtitleJobTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly FakeEngine _engine = new();
    private readonly FakeInstaller _installer = new();

    [Fact]
    public async Task Failed_startup_reports_safe_state_unavailable_and_rejects_submission()
    {
        _engine.FailReapNumber = 1;
        using var service = CreateService();
        await service.StartAsync(default);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        var capabilities = await service.GetCapabilitiesAsync(default);
        Assert.True(capabilities.Enabled);
        Assert.False(capabilities.WorkerAvailable);
        Assert.Equal("subtitle_state_unavailable", capabilities.ReasonCode);
        Assert.DoesNotContain(_directory.Path, capabilities.Detail!);
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => service.SubmitAsync(new("media", "/"), default));
        Assert.Equal("subtitle_state_unavailable", error.Code);
    }

    [Fact]
    public async Task Empty_history_read_does_not_create_feature_state()
    {
        using var service = CreateService();
        Assert.Empty(await service.ListAsync(default));
        Assert.False(Directory.Exists(Path.Combine(_directory.Path, "data", "subtitle-generation")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task One_video_generates_requested_languages_and_preserves_video(bool english, bool romanian)
    {
        _directory.Write("media/Movie ü.mkv", "original video");
        using var service = CreateService();
        var job = await service.SubmitAsync(new("media", "/Movie ü.mkv", english, romanian), default);
        Assert.Equal(SubtitleJobPhase.Queued, job.Phase);
        Assert.Empty(_engine.Paths);
        await service.ProcessNextAsync(default);
        var completed = await service.GetAsync(job.JobId, default);
        Assert.Equal(SubtitleJobPhase.Completed, completed.Phase);
        Assert.Equal(SubtitleFilePhase.Completed, Assert.Single(completed.Files).Phase);
        Assert.Equal((english, romanian), Assert.Single(_installer.Installed));
        Assert.Equal(romanian, _engine.Romanian);
        Assert.Equal("original video", File.ReadAllText(Path.Combine(_directory.Path, "media/Movie ü.mkv")));
    }

    [Fact]
    public async Task Folder_discovers_videos_recursively_once_and_continues_after_failure()
    {
        _directory.Write("media/Movie.mkv", "video");
        _directory.Write("media/sub/Other.MP4", "video");
        _directory.Write("media/sub/notes.txt", "ignore");
        _engine.FailPath = "/Movie.mkv";
        using var service = CreateService();
        var job = await service.SubmitAsync(new("media", "/"), default);
        await service.ProcessNextAsync(default);
        var result = await service.GetAsync(job.JobId, default);
        Assert.Equal(2, result.Files.Count);
        Assert.Equal(2, _engine.Paths.Distinct().Count());
        Assert.Single(result.Files, file => file.Phase == SubtitleFilePhase.Failed);
        Assert.Single(result.Files, file => file.Phase == SubtitleFilePhase.Completed);
        Assert.Single(_installer.Installed);
        Assert.DoesNotContain(_directory.Path, System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Batch_processes_only_unique_selected_videos_in_selection_order_one_at_a_time()
    {
        _directory.Write("media/First.mkv", "video");
        _directory.Write("media/sub/Second.mp4", "video");
        _directory.Write("media/Unselected.mkv", "video");
        _engine.Block = true;
        using var service = CreateService();
        string[] selected = ["/sub/Second.mp4", "/First.mkv", "/sub/./Second.mp4"];
        string[] expected = ["/sub/Second.mp4", "/First.mkv"];

        var job = await service.SubmitAsync(new("media", selected[0], Paths: selected), default);

        Assert.Equal(expected, job.Files.Select(file => file.Path));
        Assert.All(job.Files, file => Assert.Equal(SubtitleFilePhase.Pending, file.Phase));
        var execution = service.ProcessNextAsync(default);
        try
        {
            await _engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(expected[0], Assert.Single(_engine.Paths));
            Assert.Equal(new[] { SubtitleFilePhase.Running, SubtitleFilePhase.Pending },
                (await service.GetAsync(job.JobId, default)).Files.Select(file => file.Phase));
            Assert.False(await service.ProcessNextAsync(default));
            Assert.Empty(_installer.Installed);
        }
        finally { _engine.Release.TrySetResult(); }

        Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(expected, _engine.Paths);
        Assert.All((await service.GetAsync(job.JobId, default)).Files, file => Assert.Equal(SubtitleFilePhase.Completed, file.Phase));
    }

    [Fact]
    public async Task Restored_batch_revalidates_missing_files_and_continues_after_engine_failure()
    {
        var missing = _directory.Write("media/Missing.mkv", "video");
        _directory.Write("media/Fail.mkv", "video");
        _directory.Write("media/Last.mp4", "video");
        _directory.Write("media/Unselected.mkv", "video");
        _engine.FailPath = "/Fail.mkv";
        string[] selected = ["/Missing.mkv", "/Fail.mkv", "/Last.mp4"];
        Guid id;
        using (var service = CreateService())
            id = (await service.SubmitAsync(new("media", selected[0], false, true, Paths: selected), default)).JobId;
        File.Delete(missing);

        using var restarted = CreateService();
        var queued = await restarted.GetAsync(id, default);
        Assert.Equal(SubtitleJobPhase.Queued, queued.Phase);
        Assert.Equal(selected, queued.Files.Select(file => file.Path));
        Assert.True(await restarted.ProcessNextAsync(default));

        var completed = await restarted.GetAsync(id, default);
        Assert.Equal(SubtitleJobPhase.Completed, completed.Phase);
        Assert.Equal("Completed with errors", completed.Stage);
        Assert.Equal(selected, completed.Files.Select(file => file.Path));
        Assert.Equal(new[] { SubtitleFilePhase.Failed, SubtitleFilePhase.Failed, SubtitleFilePhase.Completed },
            completed.Files.Select(file => file.Phase));
        Assert.Equal(selected[1..], _engine.Paths);
        Assert.Equal((false, true), Assert.Single(_installer.Installed));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task Restored_batch_detects_changed_video_before_generating_or_skipping(bool changeLength, bool alreadyGenerated)
    {
        _directory.Write("media/First.mkv", "video");
        var changed = _directory.Write("media/Changed.mkv", "video");
        _directory.Write("media/Last.mp4", "video");
        _installer.AlreadyGenerated = alreadyGenerated;
        string[] selected = ["/First.mkv", "/Changed.mkv", "/Last.mp4"];
        var originalWriteTime = File.GetLastWriteTimeUtc(changed);
        Guid id;
        using (var service = CreateService())
            id = (await service.SubmitAsync(new("media", selected[0], Paths: selected), default)).JobId;
        if (changeLength) File.AppendAllText(changed, "new bytes");
        File.SetLastWriteTimeUtc(changed, changeLength ? originalWriteTime : originalWriteTime.AddMinutes(1));

        using var restarted = CreateService();
        Assert.True(await restarted.ProcessNextAsync(default));

        var completed = await restarted.GetAsync(id, default);
        var unchanged = alreadyGenerated ? SubtitleFilePhase.Skipped : SubtitleFilePhase.Completed;
        Assert.Equal("Completed with errors", completed.Stage);
        Assert.Equal(new[] { unchanged, SubtitleFilePhase.Failed, unchanged }, completed.Files.Select(file => file.Phase));
        Assert.Contains("changed", completed.Files[1].Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(alreadyGenerated ? [] : new[] { selected[0], selected[2] }, _engine.Paths);
    }

    [Fact]
    public async Task Batch_with_read_only_video_is_rejected_before_enqueue()
    {
        _directory.Write("media/First.mkv", "video");
        var readOnly = _directory.Write("media/ReadOnly.mp4", "video");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        using var service = CreateService();
        string[] selected = ["/First.mkv", "/ReadOnly.mp4"];

        try
        {
            var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() =>
                service.SubmitAsync(new("media", selected[0], Paths: selected), default));
            Assert.Equal("subtitle_selection_invalid", error.Code);
            Assert.Contains("read-only", error.PublicDetail, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(await service.ListAsync(default));
        }
        finally { File.SetAttributes(readOnly, File.GetAttributes(readOnly) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task Batch_continues_after_a_selected_video_becomes_read_only()
    {
        var readOnly = _directory.Write("media/ReadOnly.mp4", "video");
        _directory.Write("media/Last.mkv", "video");
        using var service = CreateService();
        string[] selected = ["/ReadOnly.mp4", "/Last.mkv"];
        var job = await service.SubmitAsync(new("media", selected[0], Paths: selected), default);
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);

        try
        {
            Assert.True(await service.ProcessNextAsync(default));
            var completed = await service.GetAsync(job.JobId, default);
            Assert.Equal(new[] { SubtitleFilePhase.Failed, SubtitleFilePhase.Completed }, completed.Files.Select(file => file.Phase));
            Assert.Contains("read-only", completed.Files[0].Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(selected[1], Assert.Single(_engine.Paths));
        }
        finally { File.SetAttributes(readOnly, File.GetAttributes(readOnly) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task Legacy_read_only_video_still_generates_subtitles_beside_the_video()
    {
        var readOnly = _directory.Write("media/ReadOnly.mp4", "video");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
        using var service = CreateService();

        try
        {
            var job = await service.SubmitAsync(new("media", "/ReadOnly.mp4"), default);
            Assert.True(await service.ProcessNextAsync(default));
            Assert.Equal(SubtitleFilePhase.Completed, Assert.Single((await service.GetAsync(job.JobId, default)).Files).Phase);
        }
        finally { File.SetAttributes(readOnly, File.GetAttributes(readOnly) & ~FileAttributes.ReadOnly); }
    }

    [Fact]
    public async Task Restored_batch_without_saved_snapshots_can_still_run()
    {
        _directory.Write("media/First.mkv", "video");
        _directory.Write("media/Last.mp4", "video");
        string[] selected = ["/First.mkv", "/Last.mp4"];
        var paths = AuthenticationDataPaths.ForRoot(_directory.CreateDirectory("data"));
        var store = new SubtitleJobStore(paths, Options.Create(new SubtitleGenerationOptions()));
        var job = await store.EnqueueAsync(new("media", selected[0], Paths: selected), default);

        using var restarted = CreateService();
        Assert.True(await restarted.ProcessNextAsync(default));
        Assert.Equal(selected, _engine.Paths);
        Assert.All((await restarted.GetAsync(job.JobId, default)).Files, file => Assert.Equal(SubtitleFilePhase.Completed, file.Phase));
    }

    [Fact]
    public async Task Queued_batch_cancellation_marks_all_pending_files_cancelled_after_restart()
    {
        _directory.Write("media/First.mkv", "video");
        _directory.Write("media/Second.mp4", "video");
        string[] selected = ["/First.mkv", "/Second.mp4"];
        Guid id;
        using (var service = CreateService())
        {
            id = (await service.SubmitAsync(new("media", selected[0], Paths: selected), default)).JobId;
            Assert.Equal(SubtitleJobPhase.Cancelled, (await service.CancelAsync(id, default)).Phase);
        }

        using var restarted = CreateService();
        Assert.False(await restarted.ProcessNextAsync(default));
        var cancelled = await restarted.GetAsync(id, default);
        Assert.Equal(SubtitleJobPhase.Cancelled, cancelled.Phase);
        Assert.Equal(selected, cancelled.Files.Select(file => file.Path));
        Assert.All(cancelled.Files, file => Assert.Equal(SubtitleFilePhase.Cancelled, file.Phase));
        Assert.Empty(_engine.Paths);
    }

    [Theory]
    [InlineData("empty", "subtitle_selection_invalid")]
    [InlineData("null-entry", "subtitle_selection_invalid")]
    [InlineData("mismatch", "subtitle_selection_invalid")]
    [InlineData("folder", "subtitle_selection_invalid")]
    [InlineData("unsupported", "video_format_unsupported")]
    [InlineData("count", "subtitle_selection_too_large")]
    [InlineData("length", "subtitle_selection_too_large")]
    [InlineData("metadata", "subtitle_selection_too_large")]
    public async Task Invalid_batch_is_rejected_before_enqueue(string selection, string expectedCode)
    {
        _directory.Write("media/First.mkv", "video");
        _directory.Write("media/Second.mp4", "video");
        _directory.Write("media/notes.txt", "text");
        const string path = "/First.mkv";
        const string second = "/Second.mp4";
        string[] selected = selection switch
        {
            "empty" => [],
            "null-entry" => [path, null!],
            "mismatch" => [second, path],
            "folder" => [path, "/"],
            "unsupported" => [path, "/notes.txt"],
            "count" => [path, second, path],
            "length" => [path, new string('x', 4097)],
            "metadata" => [path, .. Enumerable.Repeat("/" + new string('ü', 4000) + ".mkv", 50)],
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
        using var service = CreateService(maxFiles: selection == "count" ? 2 : 10000);

        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() =>
            service.SubmitAsync(new("media", path, Paths: selected), default));

        Assert.Equal(expectedCode, error.Code);
        Assert.Empty(await service.ListAsync(default));
        Assert.Empty(_engine.Paths);
    }

    [Fact]
    public async Task A_missing_batch_entry_does_not_enqueue_any_selected_video()
    {
        _directory.Write("media/First.mkv", "video");
        using var service = CreateService();
        const string path = "/First.mkv";

        await Assert.ThrowsAsync<EntryNotFoundException>(() =>
            service.SubmitAsync(new("media", path, Paths: [path, "/Missing.mkv"]), default));

        Assert.Empty(await service.ListAsync(default));
    }

    [Fact]
    public async Task Rerunning_skips_successful_videos_before_calling_engine()
    {
        _directory.Write("media/Movie.mkv", "video");
        using var service = CreateService();
        var first = await service.SubmitAsync(new("media", "/"), default);
        await service.ProcessNextAsync(default);
        _installer.AlreadyGenerated = true;
        var second = await service.SubmitAsync(new("media", "/"), default);
        await service.ProcessNextAsync(default);
        Assert.Single(_engine.Paths);
        Assert.Equal(SubtitleFilePhase.Skipped, Assert.Single((await service.GetAsync(second.JobId, default)).Files).Phase);
        Assert.Equal(SubtitleJobPhase.Completed, (await service.GetAsync(first.JobId, default)).Phase);
    }

    [Fact]
    public async Task Queued_cancellation_and_refresh_recovery_are_persistent()
    {
        _directory.Write("media/Movie.mkv", "video");
        Guid id;
        using (var service = CreateService())
        {
            id = (await service.SubmitAsync(new("media", "/Movie.mkv"), default)).JobId;
            await service.CancelAsync(id, default);
        }
        using var restarted = CreateService();
        await restarted.ProcessNextAsync(default);
        Assert.Equal(SubtitleJobPhase.Cancelled, (await restarted.GetAsync(id, default)).Phase);
        Assert.Empty(_engine.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Running_cancellation_stops_engine_without_installing(bool batch)
    {
        _directory.Write("media/Movie.mkv", "video");
        _directory.Write("media/Next.mp4", "video");
        _engine.Block = true;
        using var service = CreateService();
        var job = await service.SubmitAsync(new("media", "/Movie.mkv", Paths: batch ? ["/Movie.mkv", "/Next.mp4"] : null), default);
        var execution = service.ProcessNextAsync(default);
        await _engine.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.CancelAsync(job.JobId, default);
        await execution.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = await service.GetAsync(job.JobId, default);
        Assert.Equal(SubtitleJobPhase.Cancelled, cancelled.Phase);
        Assert.Equal(batch ? 2 : 1, cancelled.Files.Count);
        Assert.All(cancelled.Files, file => Assert.Equal(SubtitleFilePhase.Cancelled, file.Phase));
        Assert.Equal("/Movie.mkv", Assert.Single(_engine.Paths));
        Assert.Empty(_installer.Installed);
    }

    [Theory]
    [InlineData("/../outside.mkv")]
    [InlineData("C:/outside.mkv")]
    public async Task Traversal_and_physical_paths_are_rejected(string path)
    {
        using var service = CreateService();
        await Assert.ThrowsAsync<InvalidLogicalPathException>(() => service.SubmitAsync(new("media", path), default));
        Assert.Empty(_engine.Paths);
    }

    [Fact]
    public async Task Read_only_and_empty_language_selection_are_rejected()
    {
        _directory.Write("media/Movie.mkv", "video");
        using var service = CreateService(readOnly: true);
        Assert.Equal("source_read_only", (await Assert.ThrowsAsync<SubtitleGenerationException>(() =>
            service.SubmitAsync(new("media", "/Movie.mkv"), default))).Code);
        Assert.Equal("subtitle_languages_required", (await Assert.ThrowsAsync<SubtitleGenerationException>(() =>
            service.SubmitAsync(new("media", "/Movie.mkv", false, false), default))).Code);
    }

    [Fact]
    public async Task Queue_capacity_is_bounded()
    {
        _directory.Write("media/Movie.mkv", "video");
        using var service = CreateService(maxJobs: 1);
        await service.SubmitAsync(new("media", "/Movie.mkv"), default);
        Assert.Equal("subtitle_capacity_reached", (await Assert.ThrowsAsync<SubtitleGenerationException>(() =>
            service.SubmitAsync(new("media", "/Movie.mkv"), default))).Code);
    }

    [Fact]
    public async Task Restart_marks_running_job_interrupted_and_leaves_queued_job_available()
    {
        var paths = AuthenticationDataPaths.ForRoot(_directory.CreateDirectory("state"));
        var options = Options.Create(new SubtitleGenerationOptions());
        var store = new SubtitleJobStore(paths, options);
        var interrupted = await store.EnqueueAsync(new("media", "/first.mkv"), default);
        var queued = await store.EnqueueAsync(new("media", "/second.mkv"), default);
        await store.TakeNextAsync(default);
        await store.UpdateAsync(interrupted.JobId, status => status with
        {
            Files = [new("/first.mkv", SubtitleFilePhase.Running)],
        }, default);
        var restarted = new SubtitleJobStore(paths, options);
        await restarted.RecoverAsync(default);
        Assert.Equal(SubtitleJobPhase.Interrupted, (await restarted.GetAsync(interrupted.JobId, default)).Phase);
        Assert.Equal(SubtitleFilePhase.Cancelled, Assert.Single((await restarted.GetAsync(interrupted.JobId, default)).Files).Phase);
        Assert.Equal(queued.JobId, (await restarted.TakeNextAsync(default))!.Status.JobId);
    }

    [Fact]
    public async Task Explicit_regeneration_bypasses_success_receipt()
    {
        _directory.Write("media/Movie.mkv", "video");
        _installer.AlreadyGenerated = true;
        using var service = CreateService();
        await service.SubmitAsync(new("media", "/Movie.mkv", SkipExisting: false), default);
        await service.ProcessNextAsync(default);
        Assert.Single(_engine.Paths);
        Assert.Single(_installer.Installed);
    }

    [Fact]
    public async Task Transcription_failure_leaves_existing_subtitles_byte_for_byte_unchanged()
    {
        _directory.Write("media/Movie.mkv", "video");
        var subtitle = _directory.Write("media/Movie.srt", "original subtitle");
        _engine.FailPath = "/Movie.mkv";
        using var service = CreateService();
        var job = await service.SubmitAsync(new("media", "/Movie.mkv"), default);
        await service.ProcessNextAsync(default);
        Assert.Equal("original subtitle", File.ReadAllText(subtitle));
        Assert.Empty(_installer.Installed);
        Assert.Equal(SubtitleFilePhase.Failed, Assert.Single((await service.GetAsync(job.JobId, default)).Files).Phase);
    }

    [Fact]
    public async Task Oversize_history_update_is_rejected_without_breaking_restart()
    {
        var paths = AuthenticationDataPaths.ForRoot(_directory.CreateDirectory("state"));
        var options = Options.Create(new SubtitleGenerationOptions());
        var store = new SubtitleJobStore(paths, options);
        var job = await store.EnqueueAsync(new("media", "/"), default);
        var files = Enumerable.Range(0, 10000).Select(index => new SubtitleFileStatus(
            "/" + new string('x', 1000) + index + ".mkv", SubtitleFilePhase.Pending)).ToArray();
        await Assert.ThrowsAsync<SubtitleGenerationException>(() => store.UpdateAsync(job.JobId,
            current => current with { Files = files }, default));
        var restarted = new SubtitleJobStore(paths, options);
        Assert.Empty((await restarted.GetAsync(job.JobId, default)).Files);
    }

    [Fact]
    public async Task Wide_folder_limit_is_enforced_before_retaining_all_children()
    {
        var root = _directory.CreateDirectory("media");
        for (var index = 0; index < 20; index++) Directory.CreateDirectory(Path.Combine(root, index.ToString()));
        var paths = new CountingPaths(new PathSecurityService(new Catalog(new("media", "Media", root, false, true, false))));
        var discovery = new SubtitleDiscovery(paths, Options.Create(new SubtitleGenerationOptions { MaximumDirectories = 2 }));
        var exception = await Assert.ThrowsAsync<SubtitleGenerationException>(() => discovery.DiscoverAsync(new("media", "/"), default));
        Assert.Equal("subtitle_selection_too_large", exception.Code);
        Assert.True(paths.Resolutions <= 4, "Traversal retained more children than its directory cap permits.");
    }

    [Fact]
    public async Task Hosted_service_reaps_late_worker_acknowledgements_after_jobs()
    {
        _directory.Write("media/Movie.mkv", "video");
        using var service = CreateService();
        var job = await service.SubmitAsync(new("media", "/Movie.mkv"), default);
        await service.StartAsync(default);
        await _engine.ReapedAfterStartup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        Assert.Equal(SubtitleJobPhase.Completed, (await service.GetAsync(job.JobId, default)).Phase);
    }

    [Fact]
    public async Task Transient_mailbox_maintenance_failure_does_not_strand_queued_jobs()
    {
        _directory.Write("media/Movie.mkv", "video");
        _engine.FailReapNumber = 2;
        using var service = CreateService();
        var first = await service.SubmitAsync(new("media", "/Movie.mkv"), default);
        var second = await service.SubmitAsync(new("media", "/Movie.mkv"), default);
        await service.StartAsync(default);
        await _engine.ReapedAfterStartup.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(default);
        Assert.Equal(SubtitleJobPhase.Completed, (await service.GetAsync(first.JobId, default)).Phase);
        Assert.Equal(SubtitleJobPhase.Completed, (await service.GetAsync(second.JobId, default)).Phase);
    }

    private SubtitleGenerationService CreateService(bool readOnly = false, int maxJobs = 8, int maxFiles = 10000)
    {
        var root = _directory.CreateDirectory("media");
        var shared = _directory.CreateDirectory("worker");
        File.WriteAllText(Path.Combine(shared, "status.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            version = 1, updatedAt = DateTimeOffset.UtcNow, englishAvailable = true, romanianAvailable = true,
        }));
        var options = Options.Create(new SubtitleGenerationOptions
        {
            Enabled = true, MaximumQueuedJobs = maxJobs, MaximumFiles = maxFiles, SharedPath = shared,
        });
        var paths = new PathSecurityService(new Catalog(new("media", "Media", root, readOnly, true, false)));
        var store = new SubtitleJobStore(AuthenticationDataPaths.ForRoot(_directory.CreateDirectory("data")), options);
        return new SubtitleGenerationService(new SubtitleDiscovery(paths, options), store,
            _engine, _installer, options, NullLogger<SubtitleGenerationService>.Instance, new SubtitleWorkerReadiness(options));
    }

    public void Dispose() => _directory.Dispose();

    private sealed class Catalog(SourceDefinition source) : ISourceCatalog
    {
        public ValueTask<IReadOnlyList<SourceDefinition>> GetDefinitionsAsync(CancellationToken token) =>
            ValueTask.FromResult<IReadOnlyList<SourceDefinition>>([source]);
        public ValueTask<IReadOnlyList<SourceSnapshot>> GetSnapshotsAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask<SourceDefinition> GetRequiredAsync(string id, CancellationToken token) =>
            id == source.Id ? ValueTask.FromResult(source) : throw new PathConfinementException("/");
    }

    private sealed class FakeEngine : ISubtitleEngine
    {
        public List<string> Paths { get; } = [];
        public string? FailPath { get; set; }
        public bool Block { get; set; }
        public bool Romanian { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReapedAfterStartup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reaps;
        public int? FailReapNumber { get; set; }
        public Task CancelAbandonedAsync(CancellationToken token)
        {
            var count = Interlocked.Increment(ref _reaps);
            if (count == FailReapNumber) throw new IOException("Temporary sharing violation");
            if (count >= 2) ReapedAfterStartup.TrySetResult();
            return Task.CompletedTask;
        }
        public async Task<SubtitleEngineResult> GenerateAsync(SubtitleVideo video, bool romanian,
            Func<SubtitleEngineProgress, Task> progress, CancellationToken token)
        {
            Paths.Add(video.LogicalPath);
            Romanian = romanian;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(token);
            if (video.LogicalPath == FailPath) throw new SubtitleGenerationException("no_audio", "The video has no audio stream.");
            await progress(new("Transcribing", 50));
            return new("1\n00:00:00,000 --> 00:00:01,000\nHello.\n", romanian ? "1\n00:00:00,000 --> 00:00:01,000\nSalut.\n" : null);
        }
    }

    private sealed class CountingPaths(IPathSecurityService inner) : IPathSecurityService
    {
        public int Resolutions { get; private set; }
        public ValueTask<ResolvedSourcePath> ResolveAsync(string source, string path, CancellationToken token)
        {
            Resolutions++;
            return inner.ResolveAsync(source, path, token);
        }
        public ValueTask<ResolvedSourcePath> ResolveChildAsync(string source, string path, string name, CancellationToken token) =>
            inner.ResolveChildAsync(source, path, name, token);
        public ValueTask<ResolvedSourcePath> ResolveDescendantAsync(string source, string path, string relative, CancellationToken token) =>
            inner.ResolveDescendantAsync(source, path, relative, token);
    }

    private sealed class FakeInstaller : ISubtitleInstaller
    {
        public bool AlreadyGenerated { get; set; }
        public List<(bool English, bool Romanian)> Installed { get; } = [];
        public Task RecoverAsync(CancellationToken token) => Task.CompletedTask;
        public Task<bool> IsAlreadyGeneratedAsync(SubtitleVideo video, bool english, bool romanian, CancellationToken token) => Task.FromResult(AlreadyGenerated);
        public Task InstallAsync(SubtitleVideo video, bool english, bool romanian, SubtitleEngineResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Installed.Add((english, romanian));
            return Task.CompletedTask;
        }
    }
}
