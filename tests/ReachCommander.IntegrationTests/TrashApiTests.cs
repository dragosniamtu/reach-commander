using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class TrashApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Trash_and_restore_lifecycle_preserves_logical_contract()
    {
        var name = $"trash-{Guid.NewGuid():N}.jpg";
        await File.WriteAllTextAsync(Path.Combine(factory.MediaRoot, "Photos", name), "photo");
        using var client = factory.CreateClient();
        var preview = await (await client.PostAsJsonAsync(
            "/api/trash/preview-delete",
            new
            {
                sourceId = "media",
                logicalPaths = new[] { $"/Photos/{name}" },
                mode = "trash",
            })).Content.ReadFromJsonAsync<DeletePreviewResponse>();

        var submit = await client.PostAsJsonAsync(
            "/api/trash/delete",
            new { planId = preview!.PlanId, permanentDeleteConfirmed = false });
        var queued = await submit.Content.ReadFromJsonAsync<FileOperationsApiTests.StatusResponse>();
        Assert.Equal(HttpStatusCode.Accepted, submit.StatusCode);
        Assert.Equal(
            "completed",
            (await FileOperationsApiTests.WaitForTerminalAsync(client, queued!.OperationId)).Phase);

        var entries = await client.GetFromJsonAsync<TrashEntryResponse[]>("/api/trash?sourceId=media");
        var entry = Assert.Single(entries!, candidate => candidate.Name == name);
        var restorePreviewResponse = await client.PostAsJsonAsync(
            "/api/trash/preview-restore",
            new { trashIds = new[] { entry.TrashId } });
        var restorePreviewBody = await restorePreviewResponse.Content.ReadAsStringAsync();
        Assert.True(
            restorePreviewResponse.IsSuccessStatusCode,
            $"Restore preview failed: {restorePreviewResponse.StatusCode} {restorePreviewBody}");
        var restorePreview = await restorePreviewResponse.Content.ReadFromJsonAsync<RestorePreviewResponse>();
        var restore = await client.PostAsJsonAsync(
            "/api/trash/restore",
            new { planId = restorePreview!.PlanId, resolutions = Array.Empty<object>() });
        var restoreQueued = await restore.Content.ReadFromJsonAsync<FileOperationsApiTests.StatusResponse>();

        Assert.Equal(HttpStatusCode.Accepted, restore.StatusCode);
        Assert.Equal(
            "completed",
            (await FileOperationsApiTests.WaitForTerminalAsync(client, restoreQueued!.OperationId)).Phase);
        Assert.Equal("photo", await File.ReadAllTextAsync(Path.Combine(factory.MediaRoot, "Photos", name)));
        Assert.Empty(await client.GetFromJsonAsync<TrashEntryResponse[]>("/api/trash?sourceId=media") ?? []);
    }

    [Fact]
    public async Task Permanent_delete_submission_requires_confirmation()
    {
        var name = $"permanent-{Guid.NewGuid():N}.txt";
        await File.WriteAllTextAsync(Path.Combine(factory.MediaRoot, name), "keep");
        using var client = factory.CreateClient();
        var preview = await (await client.PostAsJsonAsync(
            "/api/trash/preview-delete",
            new
            {
                sourceId = "media",
                logicalPaths = new[] { $"/{name}" },
                mode = "permanent",
            })).Content.ReadFromJsonAsync<DeletePreviewResponse>();

        var response = await client.PostAsJsonAsync(
            "/api/trash/delete",
            new { planId = preview!.PlanId, permanentDeleteConfirmed = false });
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("permanent_delete_confirmation_required", problem!.Code);
        Assert.True(File.Exists(Path.Combine(factory.MediaRoot, name)));
    }

    private sealed record DeletePreviewResponse(Guid PlanId);
    private sealed record TrashEntryResponse(Guid TrashId, string Name);
    private sealed record RestorePreviewResponse(Guid PlanId);
    private sealed record ProblemResponse(string Code);
}
