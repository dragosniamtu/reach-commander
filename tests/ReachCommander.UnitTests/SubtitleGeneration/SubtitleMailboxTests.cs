using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.SubtitleGeneration;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SubtitleGeneration;

public sealed class SubtitleMailboxTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task Disappearing_worker_stops_job_after_bounded_grace_and_preserves_cancellation_marker()
    {
        WriteStatus();
        var engine = new SubtitleMailboxEngine(Options.Create(new SubtitleGenerationOptions
        {
            Enabled = true, SharedPath = _directory.Path, WorkerTimeout = TimeSpan.FromHours(6),
            PollInterval = TimeSpan.FromMilliseconds(10), WorkerUnavailableGrace = TimeSpan.FromMilliseconds(50),
        }));
        var task = engine.GenerateAsync(new("media", "/movie.mkv", "/media/movie.mkv", "/", 1, 2), false,
            _ => Task.CompletedTask, default);
        var (id, _) = await WaitForRequest();
        File.Delete(Path.Combine(_directory.Path, "status.json"));
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("subtitle_worker_unavailable", error.Code);
        Assert.True(File.Exists(Path.Combine(_directory.Path, "requests", id + ".cancel")));
    }

    [Fact]
    public async Task Healthy_heartbeat_allows_long_silent_model_work()
    {
        WriteStatus();
        var engine = new SubtitleMailboxEngine(Options.Create(new SubtitleGenerationOptions
        {
            Enabled = true, SharedPath = _directory.Path, WorkerTimeout = TimeSpan.FromSeconds(10),
            PollInterval = TimeSpan.FromMilliseconds(10), WorkerUnavailableGrace = TimeSpan.FromMilliseconds(50),
        }));
        var task = engine.GenerateAsync(new("media", "/movie.mkv", "/media/movie.mkv", "/", 1, 2), false,
            _ => Task.CompletedTask, default);
        var (id, _) = await WaitForRequest();
        await Task.Delay(200);
        Assert.False(task.IsCompleted);
        WriteResult(id, "completed", "English");
        Assert.Equal("English", (await task).EnglishSrt);
    }

    [Fact]
    public async Task Missing_worker_fails_without_publishing_a_request()
    {
        var engine = new SubtitleMailboxEngine(Options.Create(new SubtitleGenerationOptions
        {
            Enabled = true, SharedPath = _directory.Path, WorkerTimeout = TimeSpan.FromMilliseconds(50),
            PollInterval = TimeSpan.FromMilliseconds(10),
        }));
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => engine.GenerateAsync(
            new("media", "/movie.mkv", "/media/movie.mkv", "/", 1, 2), false, _ => Task.CompletedTask, default));
        Assert.Equal("subtitle_worker_unavailable", error.Code);
        Assert.False(Directory.Exists(Path.Combine(_directory.Path, "requests")));
    }

    [Fact]
    public async Task Request_uses_server_path_and_completed_result_is_consumed_and_cleaned()
    {
        var engine = CreateEngine();
        var video = new SubtitleVideo("media", "/Movie ü.mkv", "/sources/media/Movie ü.mkv", "/", 1, 2);
        var task = engine.GenerateAsync(video, true, _ => Task.CompletedTask, default);
        var (id, request) = await WaitForRequest();
        Assert.Equal(video.PhysicalPath, request.GetProperty("videoPath").GetString());
        Assert.True(request.GetProperty("romanian").GetBoolean());
        WriteResult(id, "completed", "English", "Romanian");
        var result = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new SubtitleEngineResult("English", "Romanian"), result);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory.Path, "requests")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory.Path, "results")));
    }

    [Fact]
    public async Task No_audio_error_is_safe_and_does_not_return_worker_details()
    {
        var task = CreateEngine().GenerateAsync(new("media", "/movie.mkv", "/private/movie.mkv", "/", 1, 2), false, _ => Task.CompletedTask, default);
        var (id, _) = await WaitForRequest();
        WriteResult(id, "failed", null, null, "no_audio");
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => task);
        Assert.Equal("subtitle_no_audio", error.Code);
        Assert.DoesNotContain("/private", error.PublicDetail);
    }

    [Fact]
    public async Task Cancellation_leaves_durable_marker_until_worker_acknowledges()
    {
        using var cancellation = new CancellationTokenSource();
        var task = CreateEngine().GenerateAsync(new("media", "/movie.mkv", "/media/movie.mkv", "/", 1, 2), false, _ => Task.CompletedTask, cancellation.Token);
        var (id, _) = await WaitForRequest();
        await cancellation.CancelAsync();
        await WaitUntil(() => File.Exists(Path.Combine(_directory.Path, "requests", id + ".cancel")));
        WriteResult(id, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory.Path, "requests")));
    }

    [Fact]
    public async Task Wrong_result_identifier_is_rejected()
    {
        var task = CreateEngine().GenerateAsync(new("media", "/movie.mkv", "/media/movie.mkv", "/", 1, 2), false, _ => Task.CompletedTask, default);
        var (id, _) = await WaitForRequest();
        WriteResult(id, "completed", "English", resultId: Guid.NewGuid().ToString("N"));
        var error = await Assert.ThrowsAsync<SubtitleGenerationException>(() => task);
        Assert.Equal("subtitle_worker_invalid", error.Code);
    }

    private SubtitleMailboxEngine CreateEngine()
    {
        WriteStatus();
        return new(Options.Create(new SubtitleGenerationOptions
        {
            Enabled = true, SharedPath = _directory.Path, PollInterval = TimeSpan.FromMilliseconds(10), WorkerTimeout = TimeSpan.FromSeconds(10),
        }));
    }

    private void WriteStatus() => _directory.Write("status.json", JsonSerializer.Serialize(new
    {
        version = 1, updatedAt = DateTimeOffset.UtcNow, englishAvailable = true, romanianAvailable = true,
    }));

    private async Task<(string Id, JsonElement Request)> WaitForRequest()
    {
        var directory = Path.Combine(_directory.Path, "requests");
        await WaitUntil(() => Directory.Exists(directory) && Directory.GetFiles(directory, "*.json").Length > 0);
        var path = Directory.GetFiles(directory, "*.json")[0];
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return (Path.GetFileNameWithoutExtension(path), document.RootElement.Clone());
    }

    private void WriteResult(string id, string state, string? english = null, string? romanian = null, string? error = null, string? resultId = null)
    {
        var directory = Path.Combine(_directory.Path, "results");
        var temp = Path.Combine(directory, id + ".tmp");
        File.WriteAllText(temp, JsonSerializer.Serialize(new
        {
            version = 1, id = resultId ?? id, state, stage = state, percent = 100,
            englishSrt = english, romanianSrt = romanian, errorCode = error,
        }));
        File.Move(temp, Path.Combine(directory, id + ".json"), true);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    public void Dispose() => _directory.Dispose();
}
