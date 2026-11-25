using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachCommander.Infrastructure.SubtitleGeneration;

namespace ReachCommander.IntegrationTests;

public sealed class SubtitleJobsApiTests
{
    [Fact]
    public async Task Capabilities_require_authentication()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/subtitle-jobs/capabilities")).StatusCode);
    }

    [Theory]
    [InlineData(true, true, null)]
    [InlineData(true, false, "subtitle_models_missing")]
    [InlineData(false, false, "subtitle_models_missing")]
    public async Task Capabilities_report_model_readiness_and_selected_models_gate_submission(bool english, bool romanian, string? reason)
    {
        await using var factory = new ReachCommanderApiFactory();
        WriteStatus(factory, english, romanian);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<SubtitleGenerationOptions>(options =>
            {
                options.Enabled = true;
                options.SharedPath = Path.Combine(factory.WorkspaceRoot, "worker");
            });
            services.RemoveAll<ISubtitleEngine>();
            services.AddSingleton<ISubtitleEngine>(new TestEngine());
        }));
        using var client = configured.CreateClient();
        var capabilities = await client.GetFromJsonAsync<JsonElement>("/api/subtitle-jobs/capabilities");
        Assert.True(capabilities.GetProperty("enabled").GetBoolean());
        Assert.True(capabilities.GetProperty("workerAvailable").GetBoolean());
        Assert.Equal(english, capabilities.GetProperty("englishAvailable").GetBoolean());
        Assert.Equal(romanian, capabilities.GetProperty("romanianAvailable").GetBoolean());
        Assert.Equal(reason, capabilities.GetProperty("reasonCode").GetString());
        foreach (var requestRomanian in new[] { false, true })
        {
            var response = await client.PostAsJsonAsync("/api/subtitle-jobs", new
            {
                sourceId = "media", path = "/Movies/Gladiator II.mkv", english = true, romanian = requestRomanian,
            });
            var expected = english && (!requestRomanian || romanian) ? HttpStatusCode.Accepted : HttpStatusCode.UnprocessableEntity;
            Assert.Equal(expected, response.StatusCode);
            if (expected != HttpStatusCode.Accepted)
                Assert.Equal("subtitle_models_missing", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Capabilities_are_disabled_by_default_with_exact_safe_contract()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/subtitle-jobs/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, value.EnumerateObject().Count());
        Assert.False(value.GetProperty("enabled").GetBoolean());
        Assert.False(value.GetProperty("workerAvailable").GetBoolean());
        Assert.False(value.GetProperty("englishAvailable").GetBoolean());
        Assert.False(value.GetProperty("romanianAvailable").GetBoolean());
        Assert.Equal("subtitle_generation_disabled", value.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(factory.WorkspaceRoot, value.GetRawText());
        Assert.False(Directory.Exists(Path.Combine(factory.AuthenticationDataPath, "subtitle-generation")));
    }

    [Fact]
    public async Task Submission_without_a_worker_fails_before_queuing()
    {
        await using var factory = new ReachCommanderApiFactory();
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<SubtitleGenerationOptions>(options =>
            {
                options.Enabled = true;
                options.SharedPath = Path.Combine(factory.WorkspaceRoot, "worker");
            })));
        using var client = configured.CreateClient();
        var response = await client.PostAsJsonAsync("/api/subtitle-jobs", new { sourceId = "media", path = "/Movies/Gladiator II.mkv" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("subtitle_worker_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>("/api/subtitle-jobs")).EnumerateArray());
    }

    [Fact]
    public async Task Feature_is_opt_in_and_returns_a_safe_problem_when_disabled()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/subtitle-jobs", new { sourceId = "media", path = "/Movies/Gladiator II.mkv" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("subtitle_generation_disabled", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true, false, "Hello.")]
    [InlineData(true, true, "Hello.")]
    [InlineData(false, true, "Salut.")]
    public async Task Submit_poll_and_refresh_install_named_subtitles_and_preserve_originals(bool english, bool romanian, string primary)
    {
        await using var factory = new ReachCommanderApiFactory();
        var engine = new TestEngine();
        WriteStatus(factory, true, true);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<SubtitleGenerationOptions>(options =>
            {
                options.Enabled = true;
                options.SharedPath = Path.Combine(factory.WorkspaceRoot, "worker");
            });
            services.RemoveAll<ISubtitleEngine>();
            services.AddSingleton<ISubtitleEngine>(engine);
        }));
        var original = Path.Combine(factory.MediaRoot, "Movies", "Gladiator II.ass");
        File.WriteAllText(original, "exact original bytes");
        using var client = configured.CreateClient();
        var response = await client.PostAsJsonAsync("/api/subtitle-jobs", new
        {
            sourceId = "media", path = "/Movies/Gladiator II.mkv", english, romanian, skipExisting = true,
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = status.GetProperty("jobId").GetGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        do
        {
            await Task.Delay(20, timeout.Token);
            status = await client.GetFromJsonAsync<JsonElement>($"/api/subtitle-jobs/{id}", timeout.Token);
        } while (status.GetProperty("phase").GetString() is "queued" or "discovering" or "running" or "cancelling");
        Assert.Equal("completed", status.GetProperty("phase").GetString());
        Assert.Equal("completed", status.GetProperty("files")[0].GetProperty("phase").GetString());
        Assert.Contains(primary, File.ReadAllText(Path.ChangeExtension(original, ".srt")));
        Assert.Equal(english && romanian, File.Exists(Path.Combine(factory.MediaRoot, "Movies", "Gladiator II.ro.srt")));
        Assert.Equal("exact original bytes", File.ReadAllText(Assert.Single(Directory.GetFiles(Path.GetDirectoryName(original)!, "Gladiator II*.bak"))));
        using var refreshedClient = configured.CreateClient();
        var jobs = await refreshedClient.GetFromJsonAsync<JsonElement>("/api/subtitle-jobs");
        Assert.Contains(jobs.EnumerateArray(), job => job.GetProperty("jobId").GetGuid() == id);
        Assert.DoesNotContain(factory.WorkspaceRoot, status.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, engine.Calls);
    }

    [Theory]
    [InlineData("media", "/../secret.mkv", HttpStatusCode.BadRequest)]
    [InlineData("archive", "/movie.mkv", HttpStatusCode.Forbidden)]
    public async Task Submission_enforces_authorized_path_and_writable_source(string sourceId, string path, HttpStatusCode expected)
    {
        await using var factory = new ReachCommanderApiFactory();
        File.WriteAllText(Path.Combine(factory.ArchiveRoot, "movie.mkv"), "test video");
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<SubtitleGenerationOptions>(options => { options.Enabled = true; options.SharedPath = Path.Combine(factory.WorkspaceRoot, "worker"); });
            services.RemoveAll<ISubtitleEngine>();
            services.AddSingleton<ISubtitleEngine>(new TestEngine());
        }));
        using var client = configured.CreateClient();
        var response = await client.PostAsJsonAsync("/api/subtitle-jobs", new { sourceId, path });
        Assert.Equal(expected, response.StatusCode);
        Assert.DoesNotContain(factory.WorkspaceRoot, await response.Content.ReadAsStringAsync());
    }

    private static void WriteStatus(ReachCommanderApiFactory factory, bool english, bool romanian)
    {
        var shared = Path.Combine(factory.WorkspaceRoot, "worker");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "status.json"), JsonSerializer.Serialize(new
        {
            version = 1, updatedAt = DateTimeOffset.UtcNow, englishAvailable = english, romanianAvailable = romanian,
        }));
    }

    private sealed class TestEngine : ISubtitleEngine
    {
        public int Calls { get; private set; }
        public Task CancelAbandonedAsync(CancellationToken token) => Task.CompletedTask;
        public Task<SubtitleEngineResult> GenerateAsync(SubtitleVideo video, bool romanian, Func<SubtitleEngineProgress, Task> progress, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new SubtitleEngineResult("1\n00:00:00,000 --> 00:00:01,000\nHello.\n",
                romanian ? "1\n00:00:00,000 --> 00:00:01,000\nSalut.\n" : null));
        }
    }
}
