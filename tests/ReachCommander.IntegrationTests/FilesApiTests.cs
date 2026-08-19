using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class FilesApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task List_files_returns_only_logical_metadata()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/files?sourceId=media&path=%2FMovies");
        var body = await response.Content.ReadAsStringAsync();
        var entries = await response.Content.ReadFromJsonAsync<FileResponse[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var movie = Assert.Single(entries!);
        Assert.Equal("Gladiator II.mkv", movie.Name);
        Assert.Equal("/Movies/Gladiator II.mkv", movie.RelativePath);
        Assert.Equal("file", movie.Type);
        Assert.Equal("mkv", movie.Extension);
        Assert.DoesNotContain(factory.MediaRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalPath", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task File_info_returns_one_entry()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/files/info?sourceId=media&path=%2FMovies%2FGladiator%20II.mkv");
        var entry = await response.Content.ReadFromJsonAsync<FileResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(entry);
        Assert.Equal("Gladiator II.mkv", entry.Name);
        Assert.Equal(10, entry.Size);
    }

    private sealed record FileResponse(
        string Name,
        string RelativePath,
        string Type,
        long? Size,
        DateTimeOffset ModifiedAt,
        string? Extension,
        bool IsReadOnly,
        bool IsSymbolicLink,
        string Attributes);
}
