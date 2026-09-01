using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class MediaPreviewsApiTests
{
    [Fact]
    public async Task Create_returns_logical_preview_data_without_physical_paths()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/media-previews", new
        {
            sourceId = "media",
            videoPath = "/Movies/Family Movie.mp4",
        });
        var body = await response.Content.ReadAsStringAsync();
        var preview = await response.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("direct", preview!.PlaybackMode);
        Assert.Equal("/Movies/Family Movie.srt", preview.SubtitlePath);
        Assert.DoesNotContain(factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Direct_content_supports_HTTP_byte_ranges()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        var session = await Create(client);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/media-previews/{session.SessionId}/content");
        request.Headers.Range = new RangeHeaderValue(0, 3);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("bytes 0-3/8", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Fallback_is_accepted_and_HLS_asset_names_are_narrowly_validated()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        var session = await Create(client);

        var fallback = await client.PostAsync(
            $"/api/media-previews/{session.SessionId}/fallback",
            content: null);
        var fallbackPreview = await fallback.Content.ReadFromJsonAsync<PreviewResponse>();
        var invalid = await client.GetAsync(
            $"/api/media-previews/{session.SessionId}/hls/not-a-segment.txt");

        Assert.Equal(HttpStatusCode.Accepted, fallback.StatusCode);
        Assert.Equal("queued", fallbackPreview!.Phase);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.Equal(
            "hls_asset_invalid",
            (await invalid.Content.ReadFromJsonAsync<ProblemResponse>())!.Code);
    }

    [Fact]
    public async Task Subtitle_selection_plan_and_execution_return_only_logical_paths()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        var session = await Create(client);

        var selected = await client.PutAsJsonAsync(
            $"/api/media-previews/{session.SessionId}/subtitle",
            new { subtitlePath = "/Movies/Alternate.srt" });
        var planResponse = await client.PostAsJsonAsync(
            $"/api/media-previews/{session.SessionId}/subtitle-save-plans",
            new { offsetMilliseconds = 1_400 });
        var plan = await planResponse.Content.ReadFromJsonAsync<SavePlanResponse>();
        var execute = await client.PostAsync(
            $"/api/media-previews/subtitle-save-plans/{plan!.PlanId}/execute",
            content: null);
        var body = await execute.Content.ReadAsStringAsync();
        var result = await execute.Content.ReadFromJsonAsync<SaveResultResponse>();

        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, planResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, execute.StatusCode);
        Assert.Equal("/Movies/Family Movie_original.srt", result!.BackupPath);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_is_rate_limited_with_a_media_specific_problem_code()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateClient();
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 21; attempt++)
        {
            response?.Dispose();
            response = await client.PostAsJsonAsync("/api/media-previews", new
            {
                sourceId = "media",
                videoPath = "/Movies/Family Movie.mp4",
            });
        }

        using (response)
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, response!.StatusCode);
            Assert.Equal(
                "media_preview_rate_limited",
                (await response.Content.ReadFromJsonAsync<ProblemResponse>())!.Code);
        }
    }

    private static async Task<PreviewResponse> Create(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/media-previews", new
        {
            sourceId = "media",
            videoPath = "/Movies/Family Movie.mp4",
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PreviewResponse>())!;
    }

    private sealed record PreviewResponse(
        Guid SessionId,
        string Phase,
        string PlaybackMode,
        string? SubtitlePath);

    private sealed record SavePlanResponse(Guid PlanId);

    private sealed record SaveResultResponse(string BackupPath);

    private sealed record ProblemResponse(string Code);
}
