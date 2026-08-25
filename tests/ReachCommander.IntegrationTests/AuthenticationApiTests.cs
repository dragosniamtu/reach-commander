using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.Authentication;

namespace ReachCommander.IntegrationTests;

public sealed class AuthenticationApiTests
{
    [Fact]
    public async Task Fresh_instance_exposes_only_setup_shell_health_and_auth_bootstrap_routes()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = CreateClient(factory);
        var paths = factory.Services.GetRequiredService<AuthenticationDataPaths>();
        Assert.Equal(Path.GetFullPath(factory.AuthenticationDataPath), paths.RootPath);
        Assert.False(
            File.Exists(paths.AccountPath),
            $"Unexpected administrator account at {paths.AccountPath}.");

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
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
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
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
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
    public async Task Production_http_keeps_secure_cookies_by_default()
    {
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production");

        var sessionOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var antiforgeryOptions = factory.Services
            .GetRequiredService<IOptions<AntiforgeryOptions>>()
            .Value;

        Assert.Equal(CookieSecurePolicy.Always, sessionOptions.Cookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.Always, antiforgeryOptions.Cookie.SecurePolicy);
    }

    [Fact]
    public async Task Production_trusted_lan_http_supports_setup_without_disabling_security()
    {
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production",
            configurationOverrides: new Dictionary<string, string?>
            {
                ["Authentication:AllowInsecureHttp"] = "true",
            });
        using var client = CreateClient(factory);

        var sessionOptions = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var antiforgeryOptions = factory.Services
            .GetRequiredService<IOptions<AntiforgeryOptions>>()
            .Value;
        Assert.Equal(CookieSecurePolicy.SameAsRequest, sessionOptions.Cookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, antiforgeryOptions.Cookie.SecurePolicy);

        var antiforgeryResponse = await client.GetAsync("/api/auth/antiforgery");
        var antiforgery = await antiforgeryResponse.Content.ReadFromJsonAsync<AntiforgeryResponse>();
        var antiforgeryCookie = Assert.Single(antiforgeryResponse.Headers.GetValues("Set-Cookie"));
        Assert.Equal(HttpStatusCode.OK, antiforgeryResponse.StatusCode);
        Assert.DoesNotContain("; secure", antiforgeryCookie, StringComparison.OrdinalIgnoreCase);

        client.DefaultRequestHeaders.Add("X-ReachCommander-CSRF", antiforgery!.RequestToken);
        var setupCode = await factory.GetFreshSetupCodeAsync();
        var setup = await client.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode, username = "dragos", password = "a-long-test-password" });

        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var sessionCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("ReachCommander.Session=", StringComparison.Ordinal));
        Assert.DoesNotContain("; secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sources")).StatusCode);

        client.DefaultRequestHeaders.Remove("X-ReachCommander-CSRF");
        var passwordChange = await client.PostAsJsonAsync(
            "/api/auth/password",
            new
            {
                currentPassword = "a-long-test-password",
                newPassword = "a-different-test-password",
            });
        Assert.Equal(HttpStatusCode.BadRequest, passwordChange.StatusCode);
    }

    [Fact]
    public async Task Production_antiforgery_accepts_https_from_a_trusted_proxy()
    {
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production");
        using var client = CreateClient(factory);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");

        var response = await client.GetAsync("/api/auth/antiforgery");
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_antiforgery_accepts_https_from_an_explicit_proxy()
    {
        const string proxyAddress = "10.20.30.40";
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production",
            configurationOverrides: new Dictionary<string, string?>
            {
                ["ReverseProxy:KnownProxies:0"] = proxyAddress,
            });

        var context = await factory.Server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse(proxyAddress);
            request.Request.Method = HttpMethods.Get;
            request.Request.Path = "/api/auth/antiforgery";
            request.Request.Scheme = "http";
            request.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(context.Response.Headers.TryGetValue("Set-Cookie", out var cookies));
        Assert.Contains(
            cookies,
            value => value?.Contains("secure", StringComparison.OrdinalIgnoreCase) is true);
    }

    [Fact]
    public async Task Production_ignores_https_from_an_unknown_proxy()
    {
        await using var factory = new ReachCommanderApiFactory(
            useRealSecurity: true,
            environmentName: "Production");

        var context = await factory.Server.SendAsync(request =>
        {
            request.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");
            request.Request.Method = HttpMethods.Get;
            request.Request.Path = "/health";
            request.Request.Scheme = "http";
            request.Request.Headers["X-Forwarded-Proto"] = "https";
        });

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task Setup_attempts_are_rate_limited_by_remote_address()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
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
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = CreateClient(factory);

        var response = await client.GetAsync("/api/files?sourceId=downloads&path=");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task Setup_login_password_change_and_logout_rotate_expected_sessions()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var first = factory.CreateCookieClient();
        using var second = factory.CreateCookieClient();
        await first.SetAntiforgeryAsync();
        var setupCode = await factory.GetFreshSetupCodeAsync();

        var setup = await first.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode, username = "dragos", password = "a-long-test-password" });
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var issuedCookie = Assert.Single(
            setup.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("ReachCommander.Session=", StringComparison.Ordinal));
        Assert.Contains("httponly", issuedCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", issuedCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", issuedCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("max-age=", issuedCookie, StringComparison.OrdinalIgnoreCase);

        await first.SetAntiforgeryAsync();
        await second.SetAntiforgeryAsync();
        var login = await second.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "DRAGOS", password = "a-long-test-password" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await second.SetAntiforgeryAsync();

        var changed = await first.PostAsJsonAsync(
            "/api/auth/password",
            new
            {
                currentPassword = "a-long-test-password",
                newPassword = "a-different-test-password",
            });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/sources")).StatusCode);

        using var third = factory.CreateCookieClient();
        await third.SetAntiforgeryAsync();
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await third.PostAsJsonAsync(
                "/api/auth/login",
                new { username = "dragos", password = "a-long-test-password" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await third.PostAsJsonAsync(
                "/api/auth/login",
                new { username = "dragos", password = "a-different-test-password" })).StatusCode);

        await first.SetAntiforgeryAsync();
        var logout = await first.PostAsync("/api/auth/logout", content: null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        var expiredCookie = Assert.Single(
            logout.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("ReachCommander.Session=", StringComparison.Ordinal));
        Assert.Contains("expires=", expiredCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync("/api/sources")).StatusCode);
        Assert.Equal("anonymous", (await first.GetFromJsonAsync<AuthSessionResponse>(
            "/api/auth/session"))?.State);
    }

    [Fact]
    public async Task Wrong_credentials_are_generic_and_setup_code_cannot_be_reused()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var owner = factory.CreateCookieClient();
        await owner.SetAntiforgeryAsync();
        var setupCode = await factory.GetFreshSetupCodeAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await owner.PostAsJsonAsync(
                "/api/auth/setup",
                new { setupCode, username = "dragos", password = "a-long-test-password" })).StatusCode);

        using var anonymous = factory.CreateCookieClient();
        await anonymous.SetAntiforgeryAsync();
        var unknownUser = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "nobody", password = "a-long-test-password" });
        var wrongPassword = await anonymous.PostAsJsonAsync(
            "/api/auth/login",
            new { username = "dragos", password = "this-password-is-wrong" });
        var unknownProblem = await unknownUser.Content.ReadFromJsonAsync<ProblemResponse>();
        var wrongProblem = await wrongPassword.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(unknownProblem, wrongProblem);
        Assert.Equal("invalid_credentials", unknownProblem?.Code);

        var reused = await anonymous.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode, username = "another", password = "another-long-password" });
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal(
            "administrator_exists",
            (await reused.Content.ReadFromJsonAsync<ProblemResponse>())?.Code);
    }

    [Fact]
    public async Task Anonymous_antiforgery_token_must_be_refreshed_after_sign_in()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        await client.SetAntiforgeryAsync();
        var setupCode = await factory.GetFreshSetupCodeAsync();

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                "/api/auth/setup",
                new { setupCode, username = "dragos", password = "a-long-test-password" })).StatusCode);

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsync("/api/auth/logout", content: null)).StatusCode);
        await client.SetAntiforgeryAsync();
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync("/api/auth/logout", content: null)).StatusCode);
    }

    [Fact]
    public async Task Authentication_options_cache_headers_and_cors_are_hardened()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        var options = factory.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);

        Assert.Equal(TimeSpan.FromHours(12), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);

        var session = await client.GetAsync("/api/auth/session");
        Assert.True(session.Headers.CacheControl?.NoStore);

        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        preflight.Headers.Add("Origin", "https://attacker.example");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        var cors = await client.SendAsync(preflight);
        Assert.False(cors.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(cors.Headers.Contains("Access-Control-Allow-Credentials"));

        var openApi = await client.GetAsync("/openapi/v1.json");
        Assert.NotEqual("application/json", openApi.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("\"openapi\"", await openApi.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persisted_account_and_logs_exclude_credentials_hashes_and_stamps()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        await client.SetAntiforgeryAsync();
        var setupCode = await factory.GetFreshSetupCodeAsync();

        await client.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode, username = "dragos", password = "a-long-test-password" });

        var accountJson = await File.ReadAllTextAsync(
            Path.Combine(factory.AuthenticationDataPath, "auth", "account.json"));
        using var account = JsonDocument.Parse(accountJson);
        var passwordHash = account.RootElement.GetProperty("passwordHash").GetString()!;
        var securityStamp = account.RootElement.GetProperty("securityStamp").GetString()!;
        Assert.DoesNotContain("a-long-test-password", accountJson, StringComparison.Ordinal);
        Assert.DoesNotContain(setupCode, accountJson, StringComparison.Ordinal);

        var logs = string.Join('\n', factory.LogMessages);
        Assert.DoesNotContain("a-long-test-password", logs, StringComparison.Ordinal);
        Assert.DoesNotContain(passwordHash, logs, StringComparison.Ordinal);
        Assert.DoesNotContain(securityStamp, logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Account_deletion_and_recreation_invalidates_the_old_cookie()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var oldSession = factory.CreateCookieClient();
        await oldSession.SetAntiforgeryAsync();
        var firstCode = await factory.GetFreshSetupCodeAsync();
        await oldSession.PostAsJsonAsync(
            "/api/auth/setup",
            new { setupCode = firstCode, username = "dragos", password = "a-long-test-password" });

        File.Delete(Path.Combine(factory.AuthenticationDataPath, "auth", "account.json"));
        var secondCode = await factory.GetFreshSetupCodeAsync();
        using var replacement = factory.CreateCookieClient();
        await replacement.SetAntiforgeryAsync();
        Assert.Equal(
            HttpStatusCode.OK,
            (await replacement.PostAsJsonAsync(
                "/api/auth/setup",
                new { setupCode = secondCode, username = "dragos", password = "a-different-test-password" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await oldSession.GetAsync("/api/sources")).StatusCode);
        Assert.Equal(
            "anonymous",
            (await oldSession.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session"))?.State);
    }

    [Fact]
    public async Task Malformed_account_returns_sanitized_service_unavailable()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        Assert.Equal(
            "setupRequired",
            (await client.GetFromJsonAsync<AuthSessionResponse>("/api/auth/session"))?.State);
        await File.WriteAllTextAsync(
            Path.Combine(factory.AuthenticationDataPath, "auth", "account.json"),
            "{not-valid-json");

        var response = await client.GetAsync("/api/auth/session");
        var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("authentication_state_unavailable", problem?.Code);
        Assert.DoesNotContain("not-valid-json", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replacing_key_ring_and_restarting_invalidates_existing_cookie()
    {
        var authenticationDataPath = Path.Combine(
            Path.GetTempPath(),
            $"reachcommander-auth-restart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(authenticationDataPath);
        try
        {
            string sessionCookie;
            await using (var firstFactory = new ReachCommanderApiFactory(
                useRealSecurity: true,
                authenticationDataPath: authenticationDataPath))
            {
                using var first = firstFactory.CreateCookieClient();
                await first.SetAntiforgeryAsync();
                var setupCode = await firstFactory.GetFreshSetupCodeAsync();
                var setup = await first.PostAsJsonAsync(
                    "/api/auth/setup",
                    new { setupCode, username = "dragos", password = "a-long-test-password" });
                sessionCookie = SessionCookieFrom(setup);
            }

            var keysPath = Path.Combine(authenticationDataPath, "keys");
            Directory.Delete(keysPath, recursive: true);
            Directory.CreateDirectory(keysPath);

            await using var secondFactory = new ReachCommanderApiFactory(
                useRealSecurity: true,
                authenticationDataPath: authenticationDataPath);
            using var second = secondFactory.CreateClient(new()
            {
                AllowAutoRedirect = false,
                HandleCookies = false,
            });
            second.DefaultRequestHeaders.Add("Cookie", sessionCookie);

            Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/sources")).StatusCode);
        }
        finally
        {
            try
            {
                Directory.Delete(authenticationDataPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static HttpClient CreateClient(ReachCommanderApiFactory factory) =>
        factory.CreateCookieClient();

    private static Task<HttpResponseMessage> PostInvalidSetupAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/api/auth/setup",
            new
            {
                setupCode = "not-the-real-setup-code",
                username = "dragos",
                password = "a-long-test-password",
            });

    private static string SessionCookieFrom(HttpResponseMessage response)
    {
        var header = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith("ReachCommander.Session=", StringComparison.Ordinal));
        return header.Split(';', 2)[0];
    }

    private sealed record AuthSessionResponse(string State, string? Username);

    private sealed record AntiforgeryResponse(string RequestToken);

    private sealed record ProblemResponse(string Code, string? Title = null, string? Detail = null);
}
