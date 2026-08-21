using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace ReachCommander.IntegrationTests;

internal sealed class TestAntiforgery : IAntiforgery
{
    private static readonly AntiforgeryTokenSet Tokens = new(
        "integration-test-request-token",
        "integration-test-cookie-token",
        "__RequestVerificationToken",
        "X-ReachCommander-CSRF");

    public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => Tokens;

    public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => Tokens;

    public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);

    public void SetCookieTokenAndHeader(HttpContext httpContext)
    {
    }

    public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
}
