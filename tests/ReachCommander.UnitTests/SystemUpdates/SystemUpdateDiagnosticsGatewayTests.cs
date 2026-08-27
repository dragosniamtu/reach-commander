using System.Text.Json;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class SystemUpdateDiagnosticsGatewayTests
{
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Collect_sends_v4_and_parses_only_the_exact_sanitized_snapshot()
    {
        var transport = new FixedTransport(ValidResponse());
        var gateway = new SystemUpdateDiagnosticsGateway(transport, new FixedRequestId());

        var result = await gateway.CollectAsync(CancellationToken.None);

        Assert.True(result.Complete);
        Assert.Equal(4, result.UpdaterProtocolVersion);
        Assert.Equal("stable", result.Channel);
        Assert.Equal("v1.4.0", result.CurrentVersion);
        Assert.Equal(16, result.Checks.Count);
        Assert.All(result.Checks, check => Assert.Equal(SystemUpdateDiagnosticStatus.Healthy, check.Status));
        using var request = JsonDocument.Parse(transport.Request!);
        Assert.Equal(4, request.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("collectDiagnostics", request.RootElement.GetProperty("action").GetString());
        Assert.Equal(262_144, transport.MaximumResponseBytes);
    }

    [Fact]
    public async Task Collect_rejects_unknown_fields_and_arbitrary_reason_text()
    {
        var unknown = ValidResponse().Replace(
            "\"diagnostics\":{",
            "\"diagnostics\":{\"rawLogs\":\"/srv/private token=secret\",");
        var badReason = ValidResponse().Replace(
            "docker_engine_healthy",
            "token_secret");

        foreach (var response in new[] { unknown, badReason })
        {
            var gateway = new SystemUpdateDiagnosticsGateway(
                new FixedTransport(response),
                new FixedRequestId());
            await Assert.ThrowsAsync<SystemUpdaterProtocolException>(
                () => gateway.CollectAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Collect_rejects_non_allowlisted_channel_and_version_values()
    {
        var responses = new[]
        {
            ValidResponse().Replace("\"channel\":\"stable\"", "\"channel\":\"secret_token\""),
            ValidResponse().Replace("\"currentVersion\":\"v1.4.0\"", "\"currentVersion\":\"token@secret\""),
            ValidResponse().Replace("\"currentVersion\":\"v1.4.0\"", "\"currentVersion\":\"sha256_deadbeef\""),
        };

        foreach (var response in responses)
        {
            var gateway = new SystemUpdateDiagnosticsGateway(
                new FixedTransport(response),
                new FixedRequestId());
            await Assert.ThrowsAsync<SystemUpdaterProtocolException>(
                () => gateway.CollectAsync(CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("v1.4.0-beta.1", "v1.4.0-beta.1")]
    [InlineData("edge", "edge@0123456789ab")]
    public async Task Collect_accepts_supported_channel_and_display_version_forms(
        string channel,
        string version)
    {
        var response = ValidResponse()
            .Replace("\"channel\":\"stable\"", $"\"channel\":\"{channel}\"")
            .Replace("\"currentVersion\":\"v1.4.0\"", $"\"currentVersion\":\"{version}\"");
        var gateway = new SystemUpdateDiagnosticsGateway(
            new FixedTransport(response),
            new FixedRequestId());

        var result = await gateway.CollectAsync(CancellationToken.None);

        Assert.Equal(channel, result.Channel);
        Assert.Equal(version, result.CurrentVersion);
    }

    private static string ValidResponse()
    {
        var names = new[]
        {
            "dockerEngine", "dockerCompose", "deploymentFiles", "managementCommand",
            "updateTransactions", "sourceConfiguration", "sourceAccessibility",
            "applicationData", "updateChannel", "versionState", "imageConsistency",
            "containerHealth", "updaterService", "updaterSocket", "installDiskSpace",
            "dockerDiskSpace",
        };
        var checks = names.Select(name => new Dictionary<string, object?>
        {
            ["name"] = name,
            ["status"] = "healthy",
            ["reasonCode"] = $"{Snake(name)}_healthy",
        }).ToArray();
        return JsonSerializer.Serialize(new
        {
            protocolVersion = 4,
            requestId = RequestId.ToString("D"),
            diagnostics = new
            {
                schemaVersion = 1,
                generatedAt = "2026-08-27T12:00:00Z",
                complete = true,
                updaterProtocolVersion = 4,
                channel = "stable",
                currentVersion = "v1.4.0",
                operationId = (string?)null,
                trace = (object?)null,
                checks,
            },
        });
    }

    private static string Snake(string value) => string.Concat(value.SelectMany(character =>
        char.IsUpper(character) ? new[] { '_', char.ToLowerInvariant(character) } : new[] { character })).TrimStart('_');

    private sealed class FixedRequestId : ISystemUpdateRequestIdGenerator
    {
        public Guid NewId() => RequestId;
    }

    private sealed class FixedTransport(string response) : ISystemUpdaterTransport
    {
        public string? Request { get; private set; }
        public int MaximumResponseBytes { get; private set; }

        public Task<string> ExchangeAsync(string request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string> ExchangeAsync(
            string request,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Request = request;
            MaximumResponseBytes = maximumResponseBytes;
            return Task.FromResult(response);
        }
    }
}
