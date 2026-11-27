using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.MediaPreviews;
using ReachCommander.Infrastructure.SubtitleGeneration;
using ReachCommander.UnitTests.Support;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.UnitTests.MediaPreviews;

public sealed class SubtitleSyncMailboxTests
{
    [Fact]
    public async Task Startup_and_periodic_recovery_tolerate_non_object_worker_results()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.CreateDirectory("mailbox");
        var requests = Directory.CreateDirectory(Path.Combine(root, "repair-requests")).FullName;
        var results = Directory.CreateDirectory(Path.Combine(root, "repair-results")).FullName;
        await ReadyAsync(root);
        var engine = new SubtitleSyncMailbox(Options.Create(new SubtitleGenerationOptions
            { Enabled = true, SharedPath = root }));
        using var fixture = new SubtitleSaveTests.Fixture();
        using var service = new SubtitleRepairService(fixture.Sessions, fixture.PathSecurity, engine,
            Options.Create(new MediaPreviewOptions()));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var invalid = WriteOrphan("[]");

        await service.StartAsync(timeout.Token);
        Assert.True((await service.GetCapabilitiesAsync(timeout.Token)).Available);
        Assert.True(File.Exists(Path.Combine(requests, invalid + ".cancel")));

        var scalar = WriteOrphan("true");
        var completed = WriteOrphan("completed");
        while (!File.Exists(Path.Combine(requests, scalar + ".cancel")) ||
            File.Exists(Path.Combine(requests, completed + ".json")))
        {
            Assert.False(service.ExecuteTask!.IsCompleted);
            await Task.Delay(20, timeout.Token);
        }

        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(timeout.Token);

        string WriteOrphan(string result)
        {
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(Path.Combine(requests, id + ".json"), "{}");
            File.WriteAllText(Path.Combine(results, id + ".json"), result == "completed"
                ? JsonSerializer.Serialize(new { version = 1, id, state = "completed", percent = 100 })
                : result);
            return id;
        }
    }

    [Theory]
    [InlineData(1.04, true)]
    [InlineData(7, false)]
    public async Task Mailbox_validates_transform_and_cleans_only_repair_files(double scale, bool success)
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.CreateDirectory("mailbox");
        await ReadyAsync(root);
        Directory.CreateDirectory(Path.Combine(root, "requests"));
        File.WriteAllText(Path.Combine(root, "requests", "generation.json"), "retain");
        var engine = new SubtitleSyncMailbox(Options.Create(new SubtitleGenerationOptions
            { Enabled = true, SharedPath = root, PollInterval = TimeSpan.FromMilliseconds(10) }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = engine.SynchronizeAsync("/sources/video.mp4", "1\n00:00:01,000 --> 00:00:02,000\n_\n",
            _ => Task.CompletedTask, timeout.Token);
        var requests = Path.Combine(root, "repair-requests");
        string? request;
        while ((request = Directory.Exists(requests) ? Directory.EnumerateFiles(requests, "*.json").FirstOrDefault() : null) is null)
            await Task.Delay(10, timeout.Token);
        var id = Path.GetFileNameWithoutExtension(request);
        File.WriteAllText(Path.Combine(root, "repair-results", id + ".json"), JsonSerializer.Serialize(new
        {
            version = 1, id, state = "completed", percent = 100, offsetMilliseconds = -500, timeScale = scale,
        }));
        if (success)
        {
            var result = await task;
            Assert.Equal(-500, result.OffsetMilliseconds);
            Assert.Equal(1.04, result.TimeScale);
        }
        else await Assert.ThrowsAsync<MediaPreviewException>(() => task);
        Assert.Empty(Directory.EnumerateFiles(requests));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "repair-results")));
        Assert.Equal("retain", File.ReadAllText(Path.Combine(root, "requests", "generation.json")));
    }

    private static Task ReadyAsync(string root) => File.WriteAllTextAsync(Path.Combine(root, "repair-status.json"), JsonSerializer.Serialize(new
        { version = 1, updatedAt = DateTimeOffset.UtcNow, synchronizationAvailable = true }));

    [Fact]
    public async Task Repair_is_ready_without_any_transcription_models()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.CreateDirectory("mailbox");
        await File.WriteAllTextAsync(Path.Combine(root, "repair-status.json"), JsonSerializer.Serialize(new
        {
            version = 1, updatedAt = DateTimeOffset.UtcNow, synchronizationAvailable = true,
        }));
        var engine = new SubtitleSyncMailbox(Options.Create(new SubtitleGenerationOptions { Enabled = true, SharedPath = root }));
        var capability = await engine.GetCapabilitiesAsync(default);
        Assert.True(capability.Available);
    }

    [Fact]
    public async Task Repair_rejects_stale_or_generation_only_worker()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.CreateDirectory("mailbox");
        var engine = new SubtitleSyncMailbox(Options.Create(new SubtitleGenerationOptions { Enabled = true, SharedPath = root }));
        Assert.False((await engine.GetCapabilitiesAsync(default)).Available);
        await File.WriteAllTextAsync(Path.Combine(root, "repair-status.json"), JsonSerializer.Serialize(new
        {
            version = 1, updatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), synchronizationAvailable = true,
        }));
        Assert.False((await engine.GetCapabilitiesAsync(default)).Available);
    }
}
