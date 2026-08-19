using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class ErrorContractTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Theory]
    [InlineData("/api/files?sourceId=unknown&path=%2F", HttpStatusCode.NotFound, "source_not_found")]
    [InlineData("/api/files?sourceId=media&path=%2F..%2Fsecret", HttpStatusCode.BadRequest, "invalid_path")]
    [InlineData("/api/files?sourceId=media&path=%2FMissing", HttpStatusCode.NotFound, "entry_not_found")]
    [InlineData("/api/files?sourceId=usb&path=%2F", HttpStatusCode.ServiceUnavailable, "source_unavailable")]
    public async Task File_failures_use_sanitized_problem_details(
        string requestUri,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(requestUri);
        var body = await response.Content.ReadAsStringAsync();
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(problem);
        Assert.Equal(expectedCode, problem.Code);
        Assert.DoesNotContain(factory.WorkspaceRoot, body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Symlink_escape_returns_forbidden_when_links_are_supported()
    {
        var outside = Path.Combine(factory.WorkspaceRoot, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(factory.MediaRoot, "escape-link");
        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        using var client = factory.CreateClient();
        var response = await client.GetAsync("/api/files?sourceId=media&path=%2Fescape-link");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("path_forbidden", problem?.Code);
    }

    private sealed record ProblemResponse(
        string Type,
        string Title,
        int Status,
        string Detail,
        string Code);
}
