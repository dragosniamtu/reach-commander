using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class SourcesApiTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Fact]
    public async Task Get_sources_returns_available_and_unavailable_sources_without_roots()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/sources");
        var body = await response.Content.ReadAsStringAsync();
        var sources = await response.Content.ReadFromJsonAsync<SourceResponse[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(sources);
        Assert.Equal(3, sources.Length);
        Assert.True(Assert.Single(sources, source => source.Id == "media").IsAvailable);
        var usb = Assert.Single(sources, source => source.Id == "usb");
        Assert.False(usb.IsAvailable);
        Assert.True(usb.IsReadOnly);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rootPath", body, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SourceResponse(
        string Id,
        string Name,
        bool IsAvailable,
        bool IsReadOnly,
        long? TotalBytes,
        long? UsedBytes,
        long? FreeBytes,
        bool DefaultLeft,
        bool DefaultRight);
}
