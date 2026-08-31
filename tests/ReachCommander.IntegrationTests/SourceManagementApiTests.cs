using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ReachCommander.IntegrationTests;

public sealed class SourceManagementApiTests
{
    [Fact]
    public async Task Status_is_authenticated_and_returns_sanitized_capability()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();

        using var response = await client.GetAsync("/api/source-management/status");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(payload.GetProperty("supported").GetBoolean());
        Assert.Equal("supported", payload.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain("/opt/", payload.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_requires_an_authenticated_administrator()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();

        using var response = await client.GetAsync("/api/source-management/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Add_returns_accepted_operation_and_location()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = " Archive ", hostPath = "/srv/archive", access = "readOnly" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("accepted", payload.GetProperty("phase").GetString());
        Assert.Equal(
            $"/api/source-management/operations/{payload.GetProperty("operationId").GetGuid():D}",
            response.Headers.Location?.AbsolutePath);
        Assert.Equal(1, factory.SourceManagement.AddCount);
    }

    [Theory]
    [InlineData("", "/srv/archive", "readOnly")]
    [InlineData("Archive", "relative/archive", "readOnly")]
    [InlineData("Archive", "/srv/archive", "writeAnything")]
    public async Task Add_rejects_invalid_browser_input(
        string displayName,
        string hostPath,
        string access)
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName, hostPath, access });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.SourceManagement.AddCount);
    }

    [Fact]
    public async Task Add_rejects_numeric_access_values()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = "Archive", hostPath = "/srv/archive", access = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.SourceManagement.AddCount);
    }

    [Fact]
    public async Task Operation_status_returns_terminal_public_state()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();
        var operationId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        using var response = await client.GetAsync(
            $"/api/source-management/operations/{operationId:D}");
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("completed", payload.GetProperty("phase").GetString());
        Assert.Equal("archive", payload.GetProperty("sourceId").GetString());
    }

    [Fact]
    public async Task Unsupported_installation_is_explicit_and_cannot_add()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SourceManagement.Capability = new(
            false,
            "unsupported_deployment",
            "Source management is unavailable on this installation.");
        using var client = factory.CreateCookieClient();

        var status = await client.GetFromJsonAsync<JsonElement>(
            "/api/source-management/status");
        using var add = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = "Archive", hostPath = "/srv/archive", access = "readOnly" });
        var failure = await add.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(status.GetProperty("supported").GetBoolean());
        Assert.Equal("unsupported_deployment", status.GetProperty("reasonCode").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, add.StatusCode);
        Assert.Equal("source_management_unavailable", failure.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Add_does_not_self_lease_and_blocks_other_mutations_during_restart()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SourceManagement.DrainOnAdd = true;
        using var client = factory.CreateCookieClient();

        using var add = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = "Archive", hostPath = "/srv/archive", access = "readOnly" });
        using var mutation = await client.PostAsJsonAsync(
            "/api/directories",
            new { sourceId = "media", parentLogicalPath = "/", name = "Blocked" });
        var failure = await mutation.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, add.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, mutation.StatusCode);
        Assert.Equal("system_update_in_progress", failure.GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(factory.MediaRoot, "Blocked")));
    }

    [Fact]
    public async Task Add_with_trailing_slash_does_not_self_lease()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SourceManagement.DrainOnAdd = true;
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsJsonAsync(
            "/api/source-management/sources/",
            new { displayName = "Archive", hostPath = "/srv/archive", access = "readOnly" });
        using var child = await client.PostAsJsonAsync(
            "/api/source-management/sources/child",
            new { });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, factory.SourceManagement.AddCount);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, child.StatusCode);
    }

    [Fact]
    public async Task Add_requires_antiforgery_for_real_sessions()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        await client.SetAntiforgeryAsync();
        var setupCode = await factory.GetFreshSetupCodeAsync();
        using var setup = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode, username = "dragos", password = "a-long-test-password" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        client.DefaultRequestHeaders.Remove("X-ReachCommander-CSRF");

        using var response = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = "Archive", hostPath = "/srv/archive", access = "readOnly" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.SourceManagement.AddCount);
    }

    [Fact]
    public async Task Add_failure_is_sanitized()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SourceManagement.FailAdd = true;
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = "Archive", hostPath = "/srv/private", access = "readOnly" });
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("source_management_failed", payload.GetProperty("code").GetString());
        Assert.DoesNotContain("/srv/private", payload.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("docker", payload.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_has_a_dedicated_per_ip_rate_limit()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var accepted = await client.PostAsJsonAsync(
                "/api/source-management/sources",
                new { displayName = $"Archive {attempt}", hostPath = $"/srv/archive-{attempt}", access = "readOnly" });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync(
            "/api/source-management/sources",
            new { displayName = "Archive 4", hostPath = "/srv/archive-4", access = "readOnly" });
        var payload = await limited.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("source_management_rate_limited", payload.GetProperty("code").GetString());
    }
}
