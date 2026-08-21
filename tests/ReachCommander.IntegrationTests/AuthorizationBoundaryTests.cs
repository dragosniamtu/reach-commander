using System.Net;
using System.Text;

namespace ReachCommander.IntegrationTests;

public sealed class AuthorizationBoundaryTests
{
    [Fact]
    public async Task Representative_file_management_endpoints_reject_anonymous_requests()
    {
        await using var factory = new ReachCommanderApiFactory(useRealSecurity: true);
        using var client = factory.CreateCookieClient();
        var identifier = Guid.NewGuid();
        var requests = new (HttpMethod Method, string Uri, bool HasJsonBody)[]
        {
            (HttpMethod.Get, "/api/sources", false),
            (HttpMethod.Get, "/api/files?sourceId=downloads&path=/", false),
            (HttpMethod.Get, "/api/archives/entries?sourceId=downloads&archivePath=sample.zip&path=/", false),
            (HttpMethod.Get, "/api/system-metrics", false),
            (HttpMethod.Get, "/api/uploads/limits", false),
            (HttpMethod.Post, "/api/uploads?sourceId=downloads&path=/", false),
            (HttpMethod.Post, "/api/batch-renames/preview", true),
            (HttpMethod.Post, $"/api/batch-renames/{identifier}/execute", false),
            (HttpMethod.Post, $"/api/batch-renames/{identifier}/undo", false),
            (HttpMethod.Post, "/api/archive-extractions/preview", true),
            (HttpMethod.Post, "/api/archive-extractions/plan-id/execute", false),
            (HttpMethod.Get, "/api/archive-extractions/operation-id", false),
            (HttpMethod.Post, "/api/archive-extractions/operation-id/cancel", false),
            (HttpMethod.Get, "/api/not-a-real-route", false),
        };

        foreach (var specification in requests)
        {
            using var request = new HttpRequestMessage(specification.Method, specification.Uri);
            if (specification.HasJsonBody)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
    }
}
