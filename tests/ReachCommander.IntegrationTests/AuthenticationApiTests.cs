using System.Net;
using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

public sealed class AuthenticationApiTests
{
    [Fact]
    public async Task Fresh_instance_exposes_only_setup_shell_health_and_auth_bootstrap_routes()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = CreateClient(factory);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        var protectedResponse = await client.GetAsync("/api/sources");
        Assert.Equal(HttpStatusCode.Unauthorized, protectedResponse.StatusCode);
        Assert.Null(protectedResponse.Headers.Location);
        Assert.True(protectedResponse.Headers.CacheControl?.NoStore);

        var unmatchedApi = await client.GetAsync("/api/not-a-real-route");
        Assert.Equal(HttpStatusCode.Unauthorized, unmatchedApi.StatusCode);

        var session = await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session");
        Assert.Equal("setupRequired", session?.State);
        Assert.Null(session?.Username);
    }

    [Fact]
    public async Task Unsafe_setup_without_antiforgery_header_is_rejected()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = CreateClient(factory);

        var response = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new
            {
                setupCode = "not-the-real-setup-code",
                username = "dragos",
                password = "a-long-test-password",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Antiforgery_bootstrap_uses_an_httponly_strict_cookie()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/auth/antiforgery");
        var payload = await response.Content.ReadFromJsonAsync<AntiforgeryResponse>();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(payload?.RequestToken));
        Assert.Contains("ReachCommander.Antiforgery=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_attempts_are_rate_limited_by_remote_address()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = CreateClient(factory);
        var antiforgery = await client.GetFromJsonAsync<AntiforgeryResponse>("/api/auth/antiforgery");
        client.DefaultRequestHeaders.Add("X-ReachCommander-CSRF", antiforgery!.RequestToken);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var rejected = await PostInvalidSetupAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        }

        var limited = await PostInvalidSetupAsync(client);
        var problem = await limited.Content.ReadFromJsonAsync<ProblemResponse>();
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.Equal("authentication_rate_limited", problem?.Code);
    }

    [Fact]
    public async Task Cookie_challenge_is_unauthorized_without_an_html_redirect()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/files?sourceId=downloads&path=");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static HttpClient CreateClient(ReachCommanderApiFactory factory) =>
        factory.CreateClient(new()
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    private static Task<HttpResponseMessage> PostInvalidSetupAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/auth/setup",
            new
            {
                setupCode = "not-the-real-setup-code",
                username = "dragos",
                password = "a-long-test-password",
            });

    private sealed record AuthSessionResponse(string State, string? Username);

    private sealed record AntiforgeryResponse(string RequestToken);

    private sealed record ProblemResponse(string Code);
}
