using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReachCommander.IntegrationTests;

public sealed class ArchiveBrowsingApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Get_entries_returns_only_immediate_virtual_children()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            "/api/archives/entries?sourceId=downloads&archivePath=%2Fsample.zip&path=%2FFamily");
        var body = await response.Content.ReadFromJsonAsync<ArchiveDirectoryResponse>();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(body);
        Assert.Equal("downloads", body.SourceId);
        Assert.Equal("/sample.zip", body.ArchivePath);
        Assert.Equal("/Family", body.Path);
        Assert.Equal("zip", body.Format);
        Assert.Equal(1, body.VolumeCount);
        Assert.True(body.IsReadOnly);
        Assert.Equal(["Child", "one.txt"], body.Entries.Select(entry => entry.Name));
        Assert.All(body.Entries, entry => Assert.DoesNotContain("/", entry.Name));
        Assert.All(body.Entries, entry => Assert.Equal("Archive · RO", entry.Attributes));
        Assert.DoesNotContain(factory.DownloadsRoot, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalPath", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("invalid.zip", HttpStatusCode.BadRequest, "archive_invalid")]
    [InlineData("unsupported.zip", HttpStatusCode.UnsupportedMediaType, "archive_unsupported")]
    [InlineData("encrypted.zip", HttpStatusCode.UnprocessableEntity, "archive_encrypted")]
    [InlineData("unsafe.zip", HttpStatusCode.UnprocessableEntity, "archive_entry_unsafe")]
    [InlineData("limit.zip", HttpStatusCode.RequestEntityTooLarge, "archive_limit_exceeded")]
    [InlineData("episodes.part02.rar", HttpStatusCode.Conflict, "archive_volume_secondary")]
    [InlineData("missing.part01.rar", HttpStatusCode.UnprocessableEntity, "archive_volume_set_invalid")]
    public async Task Maps_archive_failures_to_safe_problem_details(
        string archiveName,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            $"/api/archives/entries?sourceId=downloads&archivePath=%2F{archiveName}&path=%2F");
        var raw = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(raw);

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(factory.WorkspaceRoot, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.DownloadsRoot, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("physicalPath", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_missing_required_query_values()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/archives/entries");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record ArchiveDirectoryResponse(
        string SourceId,
        string ArchivePath,
        string Path,
        string Format,
        int VolumeCount,
        bool IsReadOnly,
        IReadOnlyList<ArchiveEntryResponse> Entries);

    private sealed record ArchiveEntryResponse(
        string Path,
        string Name,
        string Type,
        long? Size,
        DateTimeOffset? ModifiedAt,
        string? Extension,
        string Attributes);
}
