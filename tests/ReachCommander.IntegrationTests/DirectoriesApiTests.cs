using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class DirectoriesApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Create_directory_returns_created_logical_entry()
    {
        var name = $"Family-{Guid.NewGuid():N}";
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/directories",
            new { sourceId = "media", parentLogicalPath = "/Photos", name });
        var entry = await response.Content.ReadFromJsonAsync<FileEntryResponse>();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/Photos/{name}", entry!.RelativePath);
        Assert.True(Directory.Exists(Path.Combine(factory.MediaRoot, "Photos", name)));
        Assert.DoesNotContain(factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reserved_directory_name_returns_stable_problem()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/directories",
            new
            {
                sourceId = "media",
                parentLogicalPath = "/Photos",
                name = ".reachcommander-trash",
            });
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_directory_name", problem!.Code);
    }

    private sealed record FileEntryResponse(string RelativePath);
    private sealed record ProblemResponse(string Code);
}
