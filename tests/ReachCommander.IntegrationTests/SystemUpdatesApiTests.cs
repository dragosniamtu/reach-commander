using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.IntegrationTests;

public sealed class SystemUpdatesApiTests
{
    [Fact]
    public async Task Get_returns_sanitized_available_status()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SystemUpdates.SetAvailable();
        using var client = factory.CreateCookieClient();

        using var response = await client.GetAsync("/api/system-update");
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"phase\":\"available\"", json);
        Assert.Contains("\"targetVersion\":\"v1.4.0\"", json);
        Assert.DoesNotContain("sha256:", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/opt/", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Apply_has_no_body_and_returns_accepted_operation()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SystemUpdates.SetAvailable();
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsync("/api/system-update/apply", content: null);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("applying", payload.GetProperty("phase").GetString());
        Assert.Equal(1, factory.SystemUpdates.ApplyCount);
    }

    [Fact]
    public async Task Apply_rejects_browser_controlled_target_body()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SystemUpdates.SetAvailable();
        using var client = factory.CreateCookieClient();
        using var body = new StringContent(
            "{\"image\":\"attacker/example:latest\"}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/system-update/apply", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.SystemUpdates.ApplyCount);
    }

    [Fact]
    public async Task Apply_blocks_active_background_operations()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SystemUpdates.SetAvailable();
        factory.SystemUpdates.SetBackgroundOperationsActive(true);
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsync("/api/system-update/apply", content: null);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "system_update_blocked_by_operations",
            payload.GetProperty("code").GetString());
        Assert.Equal(0, factory.SystemUpdates.ApplyCount);
    }

    [Fact]
    public async Task Check_rate_limit_has_stable_problem_code()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SystemUpdates.SetCheckRateLimited();
        using var client = factory.CreateCookieClient();

        using var response = await client.PostAsync("/api/system-update/check", content: null);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(
            "system_update_check_rate_limited",
            payload.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Drain_returns_503_for_mutations_but_keeps_reads_available()
    {
        await using var factory = new ReachCommanderApiFactory();
        using var client = factory.CreateCookieClient();
        Assert.True(await factory.BeginSystemUpdateDrainAsync());
        try
        {
            using var mutation = await client.PostAsJsonAsync(
                "/api/directories",
                new { sourceId = "media", parentLogicalPath = "/", name = "Blocked" });
            using var read = await client.GetAsync("/api/sources");
            var payload = await mutation.Content.ReadFromJsonAsync<JsonElement>();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, mutation.StatusCode);
            Assert.Equal("system_update_in_progress", payload.GetProperty("code").GetString());
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.False(Directory.Exists(Path.Combine(factory.MediaRoot, "Blocked")));
        }
        finally
        {
            factory.CancelSystemUpdateDrain();
        }
    }

    [Fact]
    public async Task Rollback_status_survives_a_later_get()
    {
        await using var factory = new ReachCommanderApiFactory();
        factory.SystemUpdates.SetRolledBack();
        using var client = factory.CreateCookieClient();

        var first = await client.GetFromJsonAsync<JsonElement>("/api/system-update");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/system-update");

        Assert.Equal("rolledBack", first.GetProperty("phase").GetString());
        Assert.Equal("rolledBack", second.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task Check_and_apply_require_antiforgery_for_real_sessions()
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

        using var check = await client.PostAsync("/api/system-update/check", content: null);
        using var apply = await client.PostAsync("/api/system-update/apply", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, check.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, apply.StatusCode);
        Assert.Equal(0, factory.SystemUpdates.ApplyCount);
    }
}
