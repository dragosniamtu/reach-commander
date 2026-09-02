using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace ReachCommander.IntegrationTests;

public sealed class AuthorizationBoundaryTests
{
    [Fact]
    public async Task Representative_file_management_endpoints_reject_anonymous_requests()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        var identifier = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Uri, bool HasJsonBody)[]
        {
            (HttpMethod.Get, "/api/sources", false),
            (HttpMethod.Get, "/api/files?sourceId=downloads&path=/", false),
            (HttpMethod.Get, "/api/archives/entries?sourceId=downloads&archivePath=sample.zip&path=/", false),
            (HttpMethod.Get, "/api/system-metrics", false),
            (HttpMethod.Get, "/api/system-update", false),
            (HttpMethod.Post, "/api/system-update/check", false),
            (HttpMethod.Post, "/api/system-update/apply", false),
            (HttpMethod.Get, "/api/uploads/limits", false),
            (HttpMethod.Post, "/api/uploads?sourceId=downloads&path=/", false),
            (HttpMethod.Post, "/api/renames/preview", true),
            (HttpMethod.Post, "/api/batch-renames/preview", true),
            (HttpMethod.Post, $"/api/batch-renames/{identifier}/execute", false),
            (HttpMethod.Post, $"/api/batch-renames/{identifier}/undo", false),
            (HttpMethod.Post, "/api/archive-extractions/preview", true),
            (HttpMethod.Post, "/api/archive-extractions/plan-id/execute", false),
            (HttpMethod.Get, "/api/archive-extractions/operation-id", false),
            (HttpMethod.Post, "/api/archive-extractions/operation-id/cancel", false),
            (HttpMethod.Post, "/api/file-operations/preview", true),
            (HttpMethod.Post, "/api/file-operations", true),
            (HttpMethod.Get, "/api/file-operations", false),
            (HttpMethod.Get, $"/api/file-operations/{identifier}", false),
            (HttpMethod.Post, $"/api/file-operations/{identifier}/cancel", false),
            (HttpMethod.Delete, $"/api/file-operations/{identifier}", false),
            (HttpMethod.Post, "/api/directories", true),
            (HttpMethod.Get, "/api/trash", false),
            (HttpMethod.Post, "/api/trash/preview-delete", true),
            (HttpMethod.Post, "/api/trash/delete", true),
            (HttpMethod.Post, "/api/trash/preview-restore", true),
            (HttpMethod.Post, "/api/trash/restore", true),
            (HttpMethod.Delete, "/api/trash/items", true),
            (HttpMethod.Delete, "/api/trash", true),
            (HttpMethod.Post, "/api/media-previews", true),
            (HttpMethod.Get, $"/api/media-previews/{identifier}", false),
            (HttpMethod.Get, $"/api/media-previews/{identifier}/content", false),
            (HttpMethod.Post, $"/api/media-previews/{identifier}/fallback", false),
            (HttpMethod.Put, $"/api/media-previews/{identifier}/subtitle", true),
            (HttpMethod.Post, $"/api/media-previews/{identifier}/subtitle-save-plans", true),
            (HttpMethod.Post, $"/api/media-previews/subtitle-save-plans/{identifier}/execute", false),
            (HttpMethod.Delete, $"/api/media-previews/{identifier}", false),
            (HttpMethod.Post, "/api/text-encodings/preview", true),
            (HttpMethod.Post, $"/api/text-encodings/{identifier}/execute", false),
            (HttpMethod.Get, $"/api/text-encodings/operations/{identifier}", false),
            (HttpMethod.Post, $"/api/text-encodings/operations/{identifier}/cancel", false),
            (HttpMethod.Get, "/api/not-a-real-route", false),
        };

        foreach (var specification in requests)
        {
            using var request = new HttpRequestMessage(specification.Method, specification.Uri);
            if (specification.HasJsonBody)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }

    [Fact]
    public async Task File_operation_mutations_require_antiforgery_for_authenticated_sessions()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        await client.SetAntiforgeryAsync();
        var setupCode = await factory.GetFreshSetupCodeAsync();
        var setup = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode, username = "dragos", password = "a-long-test-password" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        client.DefaultRequestHeaders.Remove("X-ReachCommander-CSRF");

        var response = await client.PostAsJsonAsync(
            "/api/directories",
            new { sourceId = "media", parentLogicalPath = "/", name = "Blocked" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(factory.MediaRoot, "Blocked")));

        var originalName = $"rename-csrf-{Guid.NewGuid():N}.txt";
        var renamedName = $"renamed-csrf-{Guid.NewGuid():N}.txt";
        File.WriteAllText(Path.Combine(factory.MediaRoot, originalName), "protected");
        var renameResponse = await client.PostAsJsonAsync(
            "/api/renames/preview",
            new
            {
                sourceId = "media",
                directoryPath = "/",
                entryPath = $"/{originalName}",
                newName = renamedName,
            });

        Assert.Equal(HttpStatusCode.BadRequest, renameResponse.StatusCode);
        Assert.True(File.Exists(Path.Combine(factory.MediaRoot, originalName)));
        Assert.False(File.Exists(Path.Combine(factory.MediaRoot, renamedName)));

        var previewResponse = await client.PostAsJsonAsync(
            "/api/media-previews",
            new { sourceId = "media", videoPath = "/Movies/Gladiator II.mkv" });
        Assert.Equal(HttpStatusCode.BadRequest, previewResponse.StatusCode);

        var encodingResponse = await client.PostAsJsonAsync(
            "/api/text-encodings/preview",
            new
            {
                sourceId = "media",
                filePaths = new[] { $"/{originalName}" },
                sourceEncoding = "auto",
                outputEncoding = "utf8",
            });
        Assert.Equal(HttpStatusCode.BadRequest, encodingResponse.StatusCode);
    }
}
