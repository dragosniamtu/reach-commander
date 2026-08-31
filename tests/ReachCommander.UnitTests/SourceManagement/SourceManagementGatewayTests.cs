using System.Text.Json;
using ReachCommander.Application.SourceManagement;
using ReachCommander.Infrastructure.SourceManagement;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.UnitTests.SourceManagement;

public sealed class SourceManagementGatewayTests
{
    private static readonly Guid RequestId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OperationId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Add_sends_only_the_strict_v5_contract_and_parses_acceptance()
    {
        var transport = new StubTransport(OperationResponse("addSource", "accepted"));
        var gateway = new UnixSourceManagementGateway(
            transport,
            new FixedRequestIdGenerator(RequestId));

        var operation = await gateway.AddAsync(
            new SourceAddRequest("Archive", "/srv/archive", SourceAccess.ReadOnly),
            default);

        using var request = JsonDocument.Parse(transport.Requests.Single());
        Assert.Equal(
            ["access", "action", "displayName", "hostPath", "protocolVersion", "requestId"],
            request.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(5, request.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("addSource", request.RootElement.GetProperty("action").GetString());
        Assert.Equal("readOnly", request.RootElement.GetProperty("access").GetString());
        Assert.Equal(OperationId, operation.OperationId);
        Assert.Equal(SourceManagementPhase.Accepted, operation.Phase);
        Assert.Equal([UnixSourceManagementGateway.MaximumMessageBytes], transport.MaximumResponseBytes);
    }

    [Theory]
    [InlineData(true, "supported")]
    [InlineData(false, "installer_upgrade_required")]
    [InlineData(false, "unsupported_deployment")]
    [InlineData(false, "unsupported_platform")]
    public async Task Status_parses_only_supported_capability_combinations(
        bool supported,
        string reasonCode)
    {
        var gateway = Gateway(CapabilityResponse(supported, reasonCode));

        var capability = await gateway.GetStatusAsync(default);

        Assert.Equal(supported, capability.Supported);
        Assert.Equal(reasonCode, capability.ReasonCode);
        Assert.DoesNotContain("/opt/", capability.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("11111111-1111-1111-1111-111111111111", "33333333-3333-3333-3333-333333333333")]
    [InlineData("\"action\":\"status\"", "\"action\":\"getOperation\"")]
    public async Task Status_rejects_correlation_mismatches(
        string expected,
        string replacement)
    {
        var response = CapabilityResponse(true, "supported")
            .Replace(expected, replacement, StringComparison.Ordinal);

        await Assert.ThrowsAsync<SourceManagementProtocolIncompatibleException>(() =>
            Gateway(response).GetStatusAsync(default));
    }

    [Theory]
    [InlineData("\"supported\":true", "\"supported\":true,\"supported\":true")]
    [InlineData("\"detail\":\"Source management is available.\"", "\"detail\":\"/opt/private leaked\"")]
    [InlineData("\"payload\":{", "\"command\":\"docker\",\"payload\":{")]
    public async Task Gateway_rejects_duplicate_unknown_or_unsanitized_fields(
        string expected,
        string replacement)
    {
        var response = CapabilityResponse(true, "supported")
            .Replace(expected, replacement, StringComparison.Ordinal);

        await Assert.ThrowsAsync<SourceManagementFailedException>(() =>
            Gateway(response).GetStatusAsync(default));
    }

    [Fact]
    public async Task Get_operation_rejects_mismatched_operation_id()
    {
        var response = OperationResponse("getOperation", "completed")
            .Replace(
                OperationId.ToString("D"),
                "33333333-3333-3333-3333-333333333333",
                StringComparison.Ordinal);

        await Assert.ThrowsAsync<SourceManagementProtocolIncompatibleException>(() =>
            Gateway(response).GetOperationAsync(OperationId, default));
    }

    [Fact]
    public async Task Old_helper_protocol_maps_to_one_time_installer_upgrade_capability()
    {
        var oldResponse = """
            {"protocolVersion":3,"requestId":null,"supported":true,"phase":"unavailable"}
            """;

        var capability = await Gateway(oldResponse).GetStatusAsync(default);

        Assert.False(capability.Supported);
        Assert.Equal("installer_upgrade_required", capability.ReasonCode);
        Assert.Contains("latest installer", capability.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oldResponse, capability.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Host_validation_error_maps_to_a_sanitized_application_failure()
    {
        var response = ErrorResponse(
            "addSource",
            null,
            "validation_failed",
            "The source folder could not be accepted.");
        var gateway = Gateway(response);

        var exception = await Assert.ThrowsAsync<SourceManagementValidationException>(() =>
            gateway.AddAsync(
                new SourceAddRequest("Archive", "/srv/private", SourceAccess.ReadWrite),
                default));

        Assert.Equal("source_management_validation_failed", exception.Code);
        Assert.DoesNotContain("/srv/private", exception.PublicDetail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(
        "\"operationId\":\"22222222-2222-2222-2222-222222222222\"",
        "\"operationId\":\"22222222-2222-2222-2222-222222222222\",\"operationId\":\"22222222-2222-2222-2222-222222222222\"")]
    [InlineData(
        "11111111-1111-1111-1111-111111111111",
        "33333333-3333-3333-3333-333333333333")]
    public async Task Add_response_validation_failure_is_an_ambiguous_outcome(
        string expected,
        string replacement)
    {
        var response = OperationResponse("addSource", "accepted")
            .Replace(expected, replacement, StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<SourceManagementMutationOutcomeUnknownException>(
            () => Gateway(response).AddAsync(
                new SourceAddRequest("Archive", "/srv/archive", SourceAccess.ReadOnly),
                default));

        Assert.Equal("source_management_failed", exception.Code);
    }

    [Fact]
    public async Task Oversized_response_is_rejected_without_exposing_content()
    {
        var privateContent = "/opt/private/" + new string('x', 5000);

        var exception = await Assert.ThrowsAsync<SourceManagementFailedException>(() =>
            Gateway(privateContent).GetStatusAsync(default));

        Assert.DoesNotContain("/opt/private", exception.PublicDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new CancellingTransport();
        var gateway = new UnixSourceManagementGateway(
            transport,
            new FixedRequestIdGenerator(RequestId));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gateway.GetStatusAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, transport.ObservedToken);
    }

    [Theory]
    [InlineData(true, typeof(SourceManagementMutationOutcomeUnknownException))]
    [InlineData(false, typeof(SourceManagementUnavailableException))]
    public async Task Add_distinguishes_ambiguous_post_send_transport_failure(
        bool requestMayHaveBeenAccepted,
        Type expectedException)
    {
        var gateway = new UnixSourceManagementGateway(
            new FailingTransport(requestMayHaveBeenAccepted),
            new FixedRequestIdGenerator(RequestId));

        var exception = await Record.ExceptionAsync(() => gateway.AddAsync(
            new SourceAddRequest("Archive", "/srv/archive", SourceAccess.ReadOnly),
            default));

        Assert.IsType(expectedException, exception);
        var sourceException = Assert.IsAssignableFrom<SourceManagementException>(exception);
        Assert.Equal(
            requestMayHaveBeenAccepted
                ? "source_management_failed"
                : "source_management_unavailable",
            sourceException.Code);
        Assert.DoesNotContain("private", sourceException.PublicDetail, StringComparison.Ordinal);
    }

    private static UnixSourceManagementGateway Gateway(string response) => new(
        new StubTransport(response),
        new FixedRequestIdGenerator(RequestId));

    private static string CapabilityResponse(bool supported, string reasonCode)
    {
        var detail = reasonCode switch
        {
            "supported" => "Source management is available.",
            "installer_upgrade_required" => "Source management requires the latest installer.",
            "unsupported_deployment" => "Source management is unavailable on this installation.",
            "unsupported_platform" => "Source management is unavailable on this platform.",
            _ => throw new ArgumentOutOfRangeException(nameof(reasonCode)),
        };
        return JsonSerializer.Serialize(new
        {
            protocolVersion = 5,
            requestId = RequestId,
            action = "status",
            payload = new { supported, reasonCode, detail },
        });
    }

    private static string OperationResponse(string action, string phase)
    {
        var sourceIdentity = phase == "completed"
            ? "\"sourceId\":\"archive\",\"displayName\":\"Archive\""
            : "\"sourceId\":null,\"displayName\":null";
        var reason = phase switch
        {
            "accepted" => "accepted",
            "completed" => "completed",
            _ => "source_management_failed",
        };
        var detail = phase switch
        {
            "accepted" => "Source change accepted.",
            "completed" => "The source has been added.",
            _ => "The source-management operation could not be completed.",
        };
        return "{" +
            $"\"protocolVersion\":5,\"requestId\":\"{RequestId:D}\",\"action\":\"{action}\"," +
            $"\"payload\":{{\"operationId\":\"{OperationId:D}\",{sourceIdentity}," +
            $"\"phase\":\"{phase}\",\"reasonCode\":\"{reason}\",\"detail\":\"{detail}\"," +
            "\"createdAt\":\"2026-08-31T10:00:00Z\",\"updatedAt\":\"2026-08-31T10:00:01Z\"}}";
    }

    private static string ErrorResponse(
        string requestAction,
        Guid? operationId,
        string code,
        string detail) => JsonSerializer.Serialize(new
        {
            protocolVersion = 5,
            requestId = RequestId,
            action = "error",
            payload = new
            {
                requestAction,
                operationId,
                code,
                detail,
            },
        });

    private sealed class FixedRequestIdGenerator(Guid id) : ISourceManagementRequestIdGenerator
    {
        public Guid NewId() => id;
    }

    private sealed class StubTransport(params string[] responses) : ISystemUpdaterTransport
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> Requests { get; } = [];

        public List<int> MaximumResponseBytes { get; } = [];

        public Task<string> ExchangeAsync(string request, CancellationToken cancellationToken) =>
            ExchangeAsync(request, UnixSourceManagementGateway.MaximumMessageBytes, cancellationToken);

        public Task<string> ExchangeAsync(
            string request,
            int maximumResponseBytes,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            MaximumResponseBytes.Add(maximumResponseBytes);
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class CancellingTransport : ISystemUpdaterTransport
    {
        public CancellationToken ObservedToken { get; private set; }

        public Task<string> ExchangeAsync(string request, CancellationToken cancellationToken)
        {
            ObservedToken = cancellationToken;
            return Task.FromCanceled<string>(cancellationToken);
        }

        public Task<string> ExchangeAsync(
            string request,
            int maximumResponseBytes,
            CancellationToken cancellationToken) => ExchangeAsync(request, cancellationToken);
    }

    private sealed class FailingTransport(bool requestMayHaveBeenAccepted)
        : ISystemUpdaterTransport
    {
        public Task<string> ExchangeAsync(
            string request,
            CancellationToken cancellationToken) => throw new SystemUpdaterUnavailableException(
                "private transport failure",
                requestMayHaveBeenAccepted);

        public Task<string> ExchangeAsync(
            string request,
            int maximumResponseBytes,
            CancellationToken cancellationToken) => ExchangeAsync(request, cancellationToken);
    }
}
