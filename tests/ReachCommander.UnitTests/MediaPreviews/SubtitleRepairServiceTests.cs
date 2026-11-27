using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.UnitTests.MediaPreviews;

public sealed class SubtitleRepairServiceTests
{
    [Fact]
    public async Task Automatic_repair_only_proposes_timing_and_never_changes_original()
    {
        using var fixture = new SubtitleSaveTests.Fixture();
        var session = await fixture.OpenAsync();
        using var service = new SubtitleRepairService(fixture.Sessions, fixture.PathSecurity, new SyncEngine(),
            Options.Create(new MediaPreviewOptions()));
        await service.StartAsync(default(CancellationToken));
        await service.StartAsync(session.SessionId, default);
        var status = await WaitAsync(service, session.SessionId);
        Assert.Equal("completed", status.State);
        Assert.Equal(700, status.OffsetMilliseconds);
        Assert.Equal(1.04, status.TimeScale);
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.SubtitlePath));
        Assert.False(File.Exists(fixture.BackupPath));
        await service.StopAsync(default);
    }

    [Fact]
    public async Task Completed_estimate_is_invalidated_by_switch_reselection_and_save()
    {
        using var fixture = new SubtitleSaveTests.Fixture();
        var session = await fixture.OpenAsync();
        using var service = new SubtitleRepairService(fixture.Sessions, fixture.PathSecurity, new SyncEngine(),
            Options.Create(new MediaPreviewOptions()));
        await service.StartAsync(default(CancellationToken));
        File.Copy(fixture.SubtitlePath, Path.Combine(fixture.MoviesPath, "alternate.srt"));

        await service.StartAsync(session.SessionId, default);
        Assert.Equal("completed", (await WaitAsync(service, session.SessionId)).State);
        await fixture.Service.SelectSubtitleAsync(session.SessionId, "/Movies/alternate.srt", default);
        await AssertIdleAsync();

        await service.StartAsync(session.SessionId, default);
        Assert.Equal("completed", (await WaitAsync(service, session.SessionId)).State);
        await fixture.Service.SelectSubtitleAsync(session.SessionId, "/Movies/alternate.srt", default);
        await AssertIdleAsync();

        await service.StartAsync(session.SessionId, default);
        Assert.Equal("completed", (await WaitAsync(service, session.SessionId)).State);
        var plan = await fixture.Service.PlanSubtitleSaveAsync(session.SessionId, 1000, default);
        await fixture.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default);
        await AssertIdleAsync();
        await service.StopAsync(default);

        async Task AssertIdleAsync()
        {
            var status = await service.GetAsync(session.SessionId, default);
            Assert.Equal("idle", status.State);
            Assert.Null(status.OffsetMilliseconds);
            Assert.Null(status.TimeScale);
            Assert.Equal("idle", (await service.CancelAsync(session.SessionId, default)).State);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_subtitle_input_cancels_running_alignment(bool save)
    {
        using var fixture = new SubtitleSaveTests.Fixture();
        var session = await fixture.OpenAsync();
        var engine = new SyncEngine(block: true);
        using var service = new SubtitleRepairService(fixture.Sessions, fixture.PathSecurity, engine,
            Options.Create(new MediaPreviewOptions()));
        await service.StartAsync(default(CancellationToken));
        await service.StartAsync(session.SessionId, default);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (save)
        {
            var plan = await fixture.Service.PlanSubtitleSaveAsync(session.SessionId, 1000, default);
            await fixture.Service.ExecuteSubtitleSaveAsync(plan.PlanId, default);
        }
        else
        {
            File.Copy(fixture.SubtitlePath, Path.Combine(fixture.MoviesPath, "alternate.srt"));
            await fixture.Service.SelectSubtitleAsync(session.SessionId, "/Movies/alternate.srt", default);
        }

        await engine.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var status = await service.GetAsync(session.SessionId, default);
        Assert.Equal("idle", status.State);
        Assert.Null(status.OffsetMilliseconds);
        Assert.Null(status.TimeScale);
        await service.StopAsync(default);
    }

    [Fact]
    public async Task Closing_preview_cancels_alignment()
    {
        using var fixture = new SubtitleSaveTests.Fixture();
        var session = await fixture.OpenAsync();
        var engine = new SyncEngine(block: true);
        using var service = new SubtitleRepairService(fixture.Sessions, fixture.PathSecurity, engine,
            Options.Create(new MediaPreviewOptions()));
        await service.StartAsync(default(CancellationToken));
        await service.StartAsync(session.SessionId, default);
        await engine.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Service.CloseAsync(session.SessionId, default);
        await engine.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(fixture.OriginalBytes, File.ReadAllBytes(fixture.SubtitlePath));
        await service.StopAsync(default);
    }

    private static async Task<SubtitleRepairStatus> WaitAsync(SubtitleRepairService service, Guid session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (true)
        {
            var status = await service.GetAsync(session, timeout.Token);
            if (status.State is not ("queued" or "running" or "cancelling")) return status;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class SyncEngine(bool block = false) : ISubtitleSyncEngine
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<SubtitleRepairCapabilities> GetCapabilitiesAsync(CancellationToken token) => Task.FromResult(new SubtitleRepairCapabilities(true, true));
        public Task RecoverAsync(CancellationToken token) => Task.CompletedTask;
        public async Task<SubtitleSyncResult> SynchronizeAsync(string videoPath, string subtitleSrt, Func<SubtitleRepairStatus, Task> progress, CancellationToken token)
        {
            Assert.DoesNotContain("Hello", subtitleSrt);
            Assert.Contains("00:00:01,000", subtitleSrt);
            Started.TrySetResult();
            if (block)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return new(700, 1.04);
        }
    }
}
