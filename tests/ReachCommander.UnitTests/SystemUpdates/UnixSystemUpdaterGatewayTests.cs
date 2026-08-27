using System.Text.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SystemUpdates;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SystemUpdates;

public sealed class UnixSystemUpdaterGatewayTests
{
    [Fact]
    public async Task Gateway_sends_only_version_id_and_fixed_action()
    {
        var transport = new SequenceUpdaterTransport(Response("available"));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        await gateway.ApplyAsync(default);

        Assert.EndsWith("\n", transport.Requests.Single(), StringComparison.Ordinal);
        using var request = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal(
            ["action", "protocolVersion", "requestId"],
            request.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            "applyConfiguredChannel",
            request.RootElement.GetProperty("action").GetString());
        Assert.Equal(3, request.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("11111111-1111-1111-1111-111111111111", request.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task Gateway_prefers_v3_and_parses_sanitized_trace()
    {
        var transport = new SequenceUpdaterTransport(
            Response("applying", progressStage: "downloading"));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        var result = await gateway.CheckAsync(default);

        Assert.Equal("downloading", result.ProgressStage);
        Assert.NotNull(result.Trace);
        Assert.Equal(SystemUpdateTraceEventCode.DownloadStarted, result.Trace.Events.Single().Code);
        Assert.Equal([3], transport.ProtocolVersions);
    }

    [Fact]
    public async Task Gateway_falls_back_to_v2_only_for_exact_protocol_incompatibility()
    {
        var transport = new SequenceUpdaterTransport(
            LegacyProtocolIncompatibleResponse(),
            Response("applying", protocolVersion: 2, progressStage: "downloading"));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        var result = await gateway.CheckAsync(default);

        Assert.Null(result.Trace);
        Assert.Equal([3, 2], transport.ProtocolVersions);
    }

    [Fact]
    public async Task Gateway_retries_v1_once_for_an_old_helper()
    {
        var transport = new SequenceUpdaterTransport(
            LegacyProtocolIncompatibleResponse(),
            LegacyProtocolIncompatibleResponse(),
            Response("applying", protocolVersion: 1, progressStage: null));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        var result = await gateway.CheckAsync(default);

        Assert.Null(result.ProgressStage);
        Assert.Equal([3, 2, 1], transport.ProtocolVersions);
    }

    [Theory]
    [InlineData("unknownCode", "started")]
    [InlineData("downloadStarted", "unknownOutcome")]
    public async Task Gateway_rejects_unknown_trace_values(string code, string outcome)
    {
        var response = Response("applying", progressStage: "downloading")
            .Replace("\"code\":\"downloadStarted\"", $"\"code\":\"{code}\"", StringComparison.Ordinal)
            .Replace("\"outcome\":\"started\"", $"\"outcome\":\"{outcome}\"", StringComparison.Ordinal);
        var transport = new SequenceUpdaterTransport(response);

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(transport, new FixedRequestId()).CheckAsync(default));

        Assert.Equal([3], transport.ProtocolVersions);
    }

    [Theory]
    [InlineData("\"exitCode\":0,")]
    [InlineData("\"timeoutSeconds\":7200,")]
    [InlineData("\"command\":\"docker compose up\",")]
    public async Task Gateway_rejects_root_only_trace_fields(string field)
    {
        var response = Response("applying", progressStage: "downloading")
            .Replace("\"sequence\":7,", field + "\"sequence\":7,", StringComparison.Ordinal);

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(new SequenceUpdaterTransport(response), new FixedRequestId())
                .CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_rejects_non_increasing_trace_sequence_elapsed_and_timestamp()
    {
        var invalidTrace = """
            {"startedAt":"2026-08-25T10:00:00Z","elapsedSeconds":4,"lastActivityAt":"2026-08-25T10:00:02Z","events":[{"sequence":8,"timestamp":"2026-08-25T10:00:02Z","elapsedSeconds":2,"code":"downloadStarted","stage":"downloading","outcome":"started"},{"sequence":8,"timestamp":"2026-08-25T10:00:01Z","elapsedSeconds":1,"code":"hostActivity","stage":"downloading","outcome":"activity"}]}
            """;
        var response = Response("applying", progressStage: "downloading", traceJson: invalidTrace);

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(new SequenceUpdaterTransport(response), new FixedRequestId())
                .CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_rejects_more_than_thirty_two_trace_events()
    {
        var events = string.Join(",", Enumerable.Range(1, 33).Select(index =>
            $$"""{"sequence":{{index}},"timestamp":"2026-08-25T10:00:00Z","elapsedSeconds":{{index}},"code":"hostActivity","stage":"downloading","outcome":"activity"}"""));
        var trace = $$"""{"startedAt":"2026-08-25T10:00:00Z","elapsedSeconds":33,"lastActivityAt":"2026-08-25T10:00:00Z","events":[{{events}}]}""";

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(
                    new SequenceUpdaterTransport(Response("applying", progressStage: "downloading", traceJson: trace)),
                    new FixedRequestId())
                .CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_rejects_mismatched_request_id()
    {
        var transport = new SequenceUpdaterTransport(
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
            new SystemUpdaterGateway(new SequenceUpdaterTransport(duplicate), new FixedRequestId())
                .CheckAsync(default));
        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(new SequenceUpdaterTransport(unknown), new FixedRequestId())
                .CheckAsync(default));
    }

    [Theory]
    [InlineData("mystery")]
    [InlineData("")]
    public async Task Gateway_rejects_unknown_phase(string phase)
    {
        var gateway = new SystemUpdaterGateway(
            new SequenceUpdaterTransport(Response(phase)),
            new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_rejects_oversized_response()
    {
        var gateway = new SystemUpdaterGateway(
            new SequenceUpdaterTransport(new string('x', SystemUpdaterGateway.MaximumMessageBytes + 1)),
            new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_parses_only_sanitized_logical_state()
    {
        var gateway = new SystemUpdaterGateway(
            new SequenceUpdaterTransport(Response("available")),
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
            new SequenceUpdaterTransport(response),
            new FixedRequestId());

        var result = await gateway.CheckAsync(default);

        Assert.Equal(123, result.UpdatedAt.Millisecond);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task Gateway_rejects_unknown_progress_stage(string progressStage)
    {
        var gateway = new SystemUpdaterGateway(
            new SequenceUpdaterTransport(Response("applying", progressStage: progressStage)),
            new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_rejects_progress_outside_operation_phases()
    {
        var gateway = new SystemUpdaterGateway(
            new SequenceUpdaterTransport(Response("current", progressStage: "downloading")),
            new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_requires_and_forbids_version_specific_fields()
    {
        var missingV3 = Response("current")
            .Replace(",\"trace\":null", string.Empty, StringComparison.Ordinal);
        var missingV2 = Response("applying", protocolVersion: 2, progressStage: "downloading")
            .Replace(",\"progressStage\":\"downloading\"", string.Empty, StringComparison.Ordinal);
        var extraV1 = Response("applying", protocolVersion: 1, progressStage: null)
            .Replace("}", ",\"progressStage\":null}", StringComparison.Ordinal);

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(new SequenceUpdaterTransport(missingV3), new FixedRequestId())
                .CheckAsync(default));
        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(
                    new SequenceUpdaterTransport(
                        LegacyProtocolIncompatibleResponse(),
                        missingV2),
                    new FixedRequestId())
                .CheckAsync(default));
        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() =>
            new SystemUpdaterGateway(
                    new SequenceUpdaterTransport(
                        LegacyProtocolIncompatibleResponse(),
                        LegacyProtocolIncompatibleResponse(),
                        extraV1),
                    new FixedRequestId())
                .CheckAsync(default));
    }

    [Fact]
    public async Task Gateway_does_not_fallback_for_a_malformed_v3_response()
    {
        var malformed = Response("applying", progressStage: "downloading")
            .Replace(",\"progressStage\":\"downloading\"", string.Empty, StringComparison.Ordinal);
        var transport = new SequenceUpdaterTransport(
            malformed,
            Response("applying", protocolVersion: 1, progressStage: null));
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));

        Assert.Equal([3], transport.ProtocolVersions);
    }

    [Fact]
    public async Task Gateway_rejects_a_malformed_v1_fallback_response()
    {
        var malformedV1 = Response("applying", protocolVersion: 1, progressStage: null)
            .Replace("\"phase\":\"applying\"", "\"phase\":\"mystery\"", StringComparison.Ordinal);
        var transport = new SequenceUpdaterTransport(
            LegacyProtocolIncompatibleResponse(),
            LegacyProtocolIncompatibleResponse(),
            malformedV1);
        var gateway = new SystemUpdaterGateway(transport, new FixedRequestId());

        await Assert.ThrowsAsync<SystemUpdaterProtocolException>(() => gateway.CheckAsync(default));

        Assert.Equal([3, 2, 1], transport.ProtocolVersions);
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

    private static string Response(
        string phase,
        int protocolVersion = 3,
        string? progressStage = null,
        string? traceJson = null)
    {
        var operationId = phase is "applying" or "completed" or "rolledBack" or "failed"
            ? "\"operation-1\""
            : "null";
        var response = $$"""
            {"protocolVersion":{{protocolVersion}},"requestId":"11111111-1111-1111-1111-111111111111","supported":true,"channel":"stable","currentVersion":"v1.3.0","targetVersion":"v1.4.0","currentDigest":"sha256:{{new string('a', 64)}}","targetDigest":"sha256:{{new string('b', 64)}}","phase":"{{phase}}","reasonCode":"{{ReasonFor(phase)}}","detail":"Public detail.","operationId":{{operationId}},"lastCheckedAt":"2026-08-25T10:00:00Z","updatedAt":"2026-08-25T10:00:00Z"}
            """;
        if (protocolVersion >= 2)
        {
            response = response.Replace(
                "}",
                $",\"progressStage\":{JsonSerializer.Serialize(progressStage)}}}",
                StringComparison.Ordinal);
        }

        if (protocolVersion == 3)
        {
            traceJson ??= phase is "applying" or "completed" or "rolledBack" or "failed"
                ? """{"startedAt":"2026-08-25T10:00:00Z","elapsedSeconds":2,"lastActivityAt":"2026-08-25T10:00:02Z","events":[{"sequence":7,"timestamp":"2026-08-25T10:00:00Z","elapsedSeconds":0,"code":"downloadStarted","stage":"downloading","outcome":"started"}]}"""
                : "null";
            response = response.Replace(
                "}",
                $",\"trace\":{traceJson}}}",
                StringComparison.Ordinal);
        }

        return response;
    }

    private static string LegacyProtocolIncompatibleResponse() => """
        {"protocolVersion":1,"requestId":null,"supported":true,"channel":null,"currentVersion":null,"targetVersion":null,"currentDigest":null,"targetDigest":null,"phase":"unavailable","reasonCode":"protocol_incompatible","detail":"The host updater protocol is incompatible.","operationId":null,"lastCheckedAt":null,"updatedAt":null}
        """;

    private static string ReasonFor(string phase) => phase switch
    {
        "available" => "update_available",
        "current" => "up_to_date",
        "applying" => "update_applying",
        _ => "update_failed",
    };

    private sealed class FixedRequestId : ISystemUpdateRequestIdGenerator
    {
        public Guid NewId() => Guid.Parse("11111111-1111-1111-1111-111111111111");
    }

    private sealed class SequenceUpdaterTransport(params string[] responses) : ISystemUpdaterTransport
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Requests { get; } = [];

        public int[] ProtocolVersions => Requests
            .Select(ProtocolVersion)
            .ToArray();

        public Task<string> ExchangeAsync(string request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_responses.Dequeue());
        }

        private static int ProtocolVersion(string request)
        {
            using var document = JsonDocument.Parse(request);
            return document.RootElement.GetProperty("protocolVersion").GetInt32();
        }
    }
}
