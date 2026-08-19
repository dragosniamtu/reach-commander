using System.Net;

namespace ReachCommander.IntegrationTests;

public sealed class StaticHostingTests(ReachCommanderApiFactory factory)
    : IClassFixture<ReachCommanderApiFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    public async Task Ui_routes_return_the_single_page_application(string requestUri)
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(requestUri);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ReachCommander test shell", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_api_routes_never_fall_back_to_html()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/unknown");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
