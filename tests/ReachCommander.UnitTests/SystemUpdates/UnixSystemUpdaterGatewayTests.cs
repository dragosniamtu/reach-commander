using System.Text.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class UnixSystemUpdaterGatewayTests
{
    [Fact]
    public async Task Gateway_sends_only_version_id_and_fixed_action()
    {
        var transport = new RecordingUpdaterTransport(Response("available"));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        await gateway.ApplyAsync(default);

        Assert.EndsWith("\n", transport.SingleRequest, StringComparison.Ordinal);
        using var request = JsonDocument.Parse(transport.SingleRequest);
        Assert.Equal(
            ["action", "protocolVersion", "requestId"],
            request.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            "applyConfiguredChannel",
            request.RootElement.GetProperty("action").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", request.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Gateway_rejects_mismatched_request_id()
    {
        var transport = new RecordingUpdaterTransport(
            Response("current").Replace(
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                StringComparison.Ordinal));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        var exception = await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            gateway.CheckAsync(default));

        Assert.Equal("system_update_protocol_incompatible", exception.Code);
    }

    [Fact]
    public async Task Gateway_rejects_duplicate_and_unknown_fields()
    {
        var duplicate = Response("current").Replace(
            "\"phase\":\"current\"",
            "\"phase\":\"current\",\"phase\":\"current\"",
            StringComparison.Ordinal);
        var unknown = Response("current").Replace(
            "\"phase\":\"current\"",
            "\"phase\":\"current\",\"command\":\"docker\"",
            StringComparison.Ordinal);

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(new RecordingUpdaterTransport(duplicate), new FixedRequestId())
                .CheckAsync(default));
        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(new RecordingUpdaterTransport(unknown), new FixedRequestId())
                .CheckAsync(default));
    }

    [Theory]
    [InlineData("mystery")]
    [InlineData("")]
    public async Task Gateway_rejects_unknown_phase(string phase)
    {
        var gateway = new SystemUpdaterGateway(
            new RecordingUpdaterTransport(Response(phase)),
            new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_rejects_oversized_response()
    {
        var gateway = new SystemUpdaterGateway(
            new RecordingUpdaterTransport(new string('x', SystemUpdaterGateway.MaximumMessageBytes + 1)),
            new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_parses_only_sanitized_logical_state()
    {
        var gateway = new SystemUpdaterGateway(
            new RecordingUpdaterTransport(Response("available")),
            new FixedRequestId());

        var result = await gateway.CheckAsync(default);

        Assert.Equal("stable", result.Channel);
        Assert.Equal("v1.3.0", result.CurrentVersion);
        Assert.Equal("v1.4.0", result.TargetVersion);
        Assert.Equal("available", result.Phase);
        Assert.DoesNotContain("Digest", result.GetType().GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Gateway_accepts_host_microsecond_timestamps()
    {
        var response = Response("current").Replace(
            "2026-08-25T10:00:00Z",
            "2026-08-25T10:00:00.123456Z",
            StringComparison.Ordinal);
        var gateway = new SystemUpdaterGateway(
            new RecordingUpdaterTransport(response),
            new FixedRequestId());

        var result = await gateway.CheckAsync(default);

        Assert.Equal(123, result.UpdatedAt.Millisecond);
    }

    [Fact]
    public async Task Unix_transport_reports_a_missing_socket_as_unavailable()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"rc-missing-{Guid.NewGuid():N}.sock");
        var transport = CreateUnixTransport(socketPath);

        await Assert.ThrowsAsync<SystemUpdaterUnavailableException>(() =>
            transport.ExchangeAsync("{}\n", default));
    }

    [Fact]
    public async Task Unix_transport_sends_and_requires_one_newline_terminated_message()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"rc-{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var server = Task.Run(async () =>
            {
                using var connection = await listener.AcceptAsync();
                var buffer = new byte[64];
                var count = await connection.ReceiveAsync(buffer, SocketFlags.None);
                var request = Encoding.UTF8.GetString(buffer, 0, count);
                await connection.SendAsync(Encoding.UTF8.GetBytes("{\"ok\":true}\n"), SocketFlags.None);
                return request;
            });
            var transport = CreateUnixTransport(socketPath);

            var response = await transport.ExchangeAsync("{\"request\":true}\n", default);

            Assert.Equal("{\"ok\":true}", response);
            Assert.Equal("{\"request\":true}\n", await server);
        }
        finally
        {
            listener.Close();
            File.Delete(socketPath);
        }
    }

    private static UnixSystemUpdaterTransport CreateUnixTransport(string socketPath) =>
        new(Options.Create(new SystemUpdateOptions
        {
            SocketPath = socketPath,
            ConnectTimeout = TimeSpan.FromMilliseconds(250),
            ResponseTimeout = TimeSpan.FromMilliseconds(250),
        }));

    private static string Response(string phase) => $$"""
        {"protocolVersion":1,"requestId":"11111111-1111-1111-1111-111111111111","supported":true,"channel":"stable","currentVersion":"v1.3.0","targetVersion":"v1.4.0","currentDigest":"sha256:{{new string('a', 64)}}","targetDigest":"sha256:{{new string('b', 64)}}","phase":"{{phase}}","reasonCode":"{{ReasonFor(phase)}}","detail":"Public detail.","operationId":null,"lastCheckedAt":"2026-08-25T10:00:00Z","updatedAt":"2026-08-25T10:00:00Z"}
        """;

    private static string ReasonFor(string phase) => phase switch
    {
        "available" => "update_available",
        "current" => "up_to_date",
        _ => "update_failed",
    };

    private sealed class FixedRequestId : ISystemUpdateRequestIdGenerator
    {
        public Guid NewId() => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class RecordingUpdaterTransport(string response) : ISystemUpdaterTransport
    {
        public string SingleRequest { get; private set; } = string.Empty;

        public Task<string> ExchangeAsync(string request, CancellationToken cancellationToken)
        {
            SingleRequest = request;
            return Task.FromResult(response);
        }
    }
}
