using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Infrastructure.MediaPreviews;

namespace ReachCommander.IntegrationTests;

public sealed class SubtitleRepairApiTests
{
    [Theory]
    [InlineData("GET", "/api/media-previews/repair-capabilities")]
    [InlineData("POST", "/api/media-previews/11111111-1111-1111-1111-111111111111/repair")]
    [InlineData("GET", "/api/media-previews/11111111-1111-1111-1111-111111111111/repair")]
    [InlineData("DELETE", "/api/media-previews/11111111-1111-1111-1111-111111111111/repair")]
    public async Task Repair_requires_authentication(string method, string path)
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateClient();
        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Disabled_repair_returns_actionable_safe_capability()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        var capability = await client.GetFromJsonAsync<JsonElement>("/api/media-previews/repair-capabilities");
        Assert.False(capability.GetProperty("enabled").GetBoolean());
        Assert.False(capability.GetProperty("available").GetBoolean());
        Assert.Equal("subtitle_repair_disabled", capability.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(factory.WorkspaceRoot, capability.GetRawText());
    }

    [Fact]
    public async Task Real_encoding_sync_preview_and_save_preserve_original_and_non_romanian_text()
    {
        await using var factory = new ReachCommanderApiFactory();
        var videoPath = Path.Combine(factory.MediaRoot, "Movies", "repair.mp4");
        var subtitlePath = Path.ChangeExtension(videoPath, ".srt");
        File.WriteAllBytes(videoPath, [0, 1, 2]);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var original = Encoding.GetEncoding(1251).GetBytes("1\r\n00:00:10,000 --> 00:00:12,000\r\nПривіт, світе!\r\n");
        File.WriteAllBytes(subtitlePath, original);
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMediaPreviewService>();
            services.AddSingleton<IMediaPreviewService>(provider => provider.GetRequiredService<MediaPreviewService>());
            services.RemoveAll<IMediaProbeRunner>();
            services.AddSingleton<IMediaProbeRunner>(new Probe());
            services.RemoveAll<ISubtitleSyncEngine>();
            services.AddSingleton<ISubtitleSyncEngine>(new Aligner());
        }));
        using var client = configured.CreateClient();
        var opened = await client.PostAsJsonAsync("/api/media-previews", new { sourceId = "media", videoPath = "/Movies/repair.mp4" });
        opened.EnsureSuccessStatusCode();
        var session = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetGuid();
        var endpoint = $"/api/media-previews/{session}";
        var selected = await client.PutAsJsonAsync(endpoint + "/subtitle", new
            { subtitlePath = "/Movies/repair.srt", sourceEncoding = "windows1251" });
        selected.EnsureSuccessStatusCode();
        var review = await selected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(review.GetProperty("subtitleEncoding").GetProperty("requiresReview").GetBoolean());
        Assert.Contains("Привіт", review.GetProperty("subtitleEncoding").GetProperty("previewText").GetString());
        var blocked = await client.PostAsync(endpoint + "/repair", null);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        (await client.PutAsJsonAsync(endpoint + "/subtitle", new
            { subtitlePath = "/Movies/repair.srt", sourceEncoding = "windows1251", encodingReviewed = true })).EnsureSuccessStatusCode();
        (await client.PostAsync(endpoint + "/repair", null)).EnsureSuccessStatusCode();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        JsonElement status;
        do
        {
            status = await client.GetFromJsonAsync<JsonElement>(endpoint + "/repair", timeout.Token);
            if (status.GetProperty("state").GetString() == "completed") break;
            await Task.Delay(10, timeout.Token);
        } while (true);
        Assert.Equal(original, File.ReadAllBytes(subtitlePath));
        var planResponse = await client.PostAsJsonAsync(endpoint + "/subtitle-save-plans", new
            { offsetMilliseconds = status.GetProperty("offsetMilliseconds").GetInt64(), timeScale = status.GetProperty("timeScale").GetDouble() });
        planResponse.EnsureSuccessStatusCode();
        var plan = await planResponse.Content.ReadFromJsonAsync<JsonElement>();
        var planId = plan.GetProperty("planId").GetGuid();
        (await client.PostAsync($"/api/media-previews/subtitle-save-plans/{planId}/execute", null)).EnsureSuccessStatusCode();
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(factory.MediaRoot, "Movies", "repair_original.srt")));
        var repaired = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(subtitlePath));
        Assert.Contains("Привіт, світе!", repaired);
        Assert.Contains("00:00:10,900 --> 00:00:12,980", repaired);
        Assert.Equal(new byte[] { 0, 1, 2 }, File.ReadAllBytes(videoPath));
    }

    private sealed class Probe : IMediaProbeRunner
    {
        public ValueTask<MediaProbeResult> ProbeAsync(string inputPhysicalPath, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MediaProbeResult("mov,mp4", "h264", "aac", 60000));
    }
    private sealed class Aligner : ISubtitleSyncEngine
    {
        public Task<SubtitleRepairCapabilities> GetCapabilitiesAsync(CancellationToken token) => Task.FromResult(new SubtitleRepairCapabilities(true, true));
        public Task RecoverAsync(CancellationToken token) => Task.CompletedTask;
        public Task<SubtitleSyncResult> SynchronizeAsync(string videoPath, string subtitleSrt,
            Func<SubtitleRepairStatus, Task> progress, CancellationToken token) => Task.FromResult(new SubtitleSyncResult(500, 1.04));
    }
}
