using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class FileOperationsApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Copy_preview_and_submit_return_logical_terminal_status()
    {
        var name = $"copy-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(factory.MediaRoot, name), "copy-data");
        using var client = factory.CreateClient();

        var previewResponse = await client.PostAsJsonAsync(
            "/api/file-operations/preview",
            new
            {
                kind = "copy",
                sourceId = "media",
                logicalPaths = new[] { $"/{name}" },
                destinationSourceId = "downloads",
                destinationLogicalDirectory = "/Complete",
            });
        var preview = await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>();

        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var submit = await client.PostAsJsonAsync(
            "/api/file-operations",
            new { planId = preview!.PlanId, resolutions = Array.Empty<object>() });
        var queued = await submit.Content.ReadFromJsonAsync<StatusResponse>();
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);

        var terminal = await WaitForTerminalAsync(client, queued!.OperationId);
        var body = await (await client.GetAsync($"/api/file-operations/{queued.OperationId}"))
            .Content.ReadAsStringAsync();
        Assert.Equal("completed", terminal.Phase);
        Assert.Equal("copy-data", await File.ReadAllTextAsync(
            Path.Combine(factory.DownloadsRoot, "Complete", name)));
        Assert.DoesNotContain(factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.DownloadsRoot, body, StringComparison.OrdinalIgnoreCase);

        var acknowledge = await client.DeleteAsync($"/api/file-operations/{queued.OperationId}");
        Assert.Equal(HttpStatusCode.NoContent, acknowledge.StatusCode);
    }

    [Fact]
    public async Task Missing_conflict_resolution_returns_sanitized_conflict()
    {
        var name = $"conflict-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(factory.MediaRoot, name), "source");
        await File.WriteAllTextAsync(Path.Combine(factory.DownloadsRoot, "Complete", name), "destination");
        using var client = factory.CreateClient();
        var preview = await (await client.PostAsJsonAsync(
            "/api/file-operations/preview",
            new
            {
                kind = "copy",
                sourceId = "media",
                logicalPaths = new[] { $"/{name}" },
                destinationSourceId = "downloads",
                destinationLogicalDirectory = "/Complete",
            })).Content.ReadFromJsonAsync<PreviewResponse>();

        var response = await client.PostAsJsonAsync(
            "/api/file-operations",
            new { planId = preview!.PlanId, resolutions = Array.Empty<object>() });
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("destination_conflict", problem!.Code);
        Assert.Equal("destination", await File.ReadAllTextAsync(
            Path.Combine(factory.DownloadsRoot, "Complete", name)));
    }

    internal static async Task<StatusResponse> WaitForTerminalAsync(
        HttpClient client,
        Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>(
                $"/api/file-operations/{operationId}",
                timeout.Token);
            if (status!.Phase is "completed" or "completedWithErrors" or "cancelled" or "failed" or "interrupted")
            {
                return status;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    internal sealed record PreviewResponse(Guid PlanId);
    internal sealed record StatusResponse(Guid OperationId, string Phase);
    private sealed record ProblemResponse(string Code);
}
