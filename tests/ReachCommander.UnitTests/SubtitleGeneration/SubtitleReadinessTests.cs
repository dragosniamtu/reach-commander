using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SubtitleGeneration;
using ReachCommander.UnitTests.Support;

namespace ReachCommander.UnitTests.SubtitleGeneration;

public sealed class SubtitleReadinessTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task Disabled_readiness_does_not_create_shared_directory()
    {
        var root = Path.Combine(_directory.Path, "absent");
        var readiness = new SubtitleWorkerReadiness(Options.Create(new SubtitleGenerationOptions { SharedPath = root }));
        var result = await readiness.GetCapabilitiesAsync(default);
        Assert.False(result.Enabled);
        Assert.Equal("subtitle_generation_disabled", result.ReasonCode);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, "subtitle_models_missing")]
    [InlineData(false, false, "subtitle_models_missing")]
    public async Task Fresh_status_reports_language_readiness(bool english, bool romanian, string? reason)
    {
        WriteStatus(DateTimeOffset.UtcNow, english, romanian);
        var result = await Create().GetCapabilitiesAsync(default);
        Assert.True(result.Enabled);
        Assert.True(result.WorkerAvailable);
        Assert.Equal(english, result.EnglishAvailable);
        Assert.Equal(romanian, result.RomanianAvailable);
        Assert.Equal(reason, result.ReasonCode);
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(10)]
    public async Task Stale_or_future_status_is_unavailable(int seconds)
    {
        WriteStatus(DateTimeOffset.UtcNow.AddSeconds(seconds), true, true);
        await AssertUnavailable();
    }

    [Fact]
    public async Task Missing_status_is_unavailable_without_creating_directories() => await AssertUnavailable();

    [Fact]
    public async Task Timestamp_without_explicit_utc_offset_is_rejected()
    {
        _directory.Write("status.json", JsonSerializer.Serialize(new
        {
            version = 1, updatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss"),
            englishAvailable = true, romanianAvailable = true,
        }));
        await AssertUnavailable();
    }

    [Fact]
    public async Task Slight_clock_skew_is_allowed()
    {
        WriteStatus(DateTimeOffset.UtcNow.AddSeconds(3), true, true);
        Assert.True((await Create().GetCapabilitiesAsync(default)).WorkerAvailable);
    }

    [Theory]
    [InlineData("{}")] // Missing booleans must not deserialize as defaults.
    [InlineData("not json")]
    [InlineData("{\"version\":1,\"version\":1,\"updatedAt\":\"2099-01-01T00:00:00Z\",\"englishAvailable\":true,\"romanianAvailable\":true}")]
    [InlineData("{\"version\":1,\"updatedAt\":\"2026-01-01\",\"englishAvailable\":\"true\",\"romanianAvailable\":false}")]
    public async Task Invalid_status_fails_closed(string json)
    {
        _directory.Write("status.json", json);
        await AssertUnavailable();
    }

    [Fact]
    public async Task Oversized_status_fails_closed()
    {
        _directory.Write("status.json", new string(' ', 4097));
        await AssertUnavailable();
    }

    [Fact]
    public async Task Inconsistent_language_status_fails_closed()
    {
        WriteStatus(DateTimeOffset.UtcNow, false, true);
        await AssertUnavailable();
    }

    [Fact]
    public async Task Directory_in_place_of_status_fails_closed()
    {
        _directory.CreateDirectory("status.json");
        await AssertUnavailable();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_or_shared_root_symlinks_are_unavailable(bool linkRoot)
    {
        if (OperatingSystem.IsWindows()) return; // Windows developer mode/administrator privilege is required.
        var target = _directory.CreateDirectory("actual");
        File.WriteAllText(Path.Combine(target, "status.json"), JsonSerializer.Serialize(new
        {
            version = 1, updatedAt = DateTimeOffset.UtcNow, englishAvailable = true, romanianAvailable = true,
        }));
        if (linkRoot)
        {
            var link = Path.Combine(_directory.Path, "linked");
            Directory.CreateSymbolicLink(link, target);
            var result = await new SubtitleWorkerReadiness(Options.Create(new SubtitleGenerationOptions
            {
                Enabled = true, SharedPath = link,
            })).GetCapabilitiesAsync(default);
            Assert.Equal("subtitle_worker_unavailable", result.ReasonCode);
            Assert.False(result.WorkerAvailable);
        }
        else
        {
            File.CreateSymbolicLink(Path.Combine(_directory.Path, "status.json"), Path.Combine(target, "status.json"));
            await AssertUnavailable();
        }
    }

    private SubtitleWorkerReadiness Create() => new(Options.Create(new SubtitleGenerationOptions
    {
        Enabled = true, SharedPath = _directory.Path,
    }));

    private void WriteStatus(DateTimeOffset time, bool english, bool romanian) => _directory.Write("status.json",
        JsonSerializer.Serialize(new { version = 1, updatedAt = time, englishAvailable = english, romanianAvailable = romanian }));

    private async Task AssertUnavailable()
    {
        var result = await Create().GetCapabilitiesAsync(default);
        Assert.True(result.Enabled);
        Assert.False(result.WorkerAvailable);
        Assert.False(result.EnglishAvailable);
        Assert.False(result.RomanianAvailable);
        Assert.Equal("subtitle_worker_unavailable", result.ReasonCode);
        Assert.DoesNotContain(_directory.Path, result.Detail!);
    }

    public void Dispose() => _directory.Dispose();
}
