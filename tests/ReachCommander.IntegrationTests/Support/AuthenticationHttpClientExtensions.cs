using System.Net.Http.Json;

namespace ReachCommander.IntegrationTests;

internal static class AuthenticationHttpClientExtensions
{
    public static async Task SetAntiforgeryAsync(this HttpClient client)
    {
        var antiforgery = await client.GetFromJsonAsync<AntiforgeryResponse>(
            "/api/auth/antiforgery");
        client.DefaultRequestHeaders.Remove("X-ReachCommander-CSRF");
        client.DefaultRequestHeaders.Add(
            "X-ReachCommander-CSRF",
            antiforgery!.RequestToken);
    }

    private sealed record AntiforgeryResponse(string RequestToken);
}
