using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReachCommander.Application.SourceManagement;
using ReachCommander.Infrastructure.SystemUpdates;

namespace ReachCommander.Infrastructure.SourceManagement;

internal sealed class UnixSourceManagementGateway(
    ISystemUpdaterTransport transport,
    ISourceManagementRequestIdGenerator requestIds) : ISourceManagementGateway
{
    public const int MaximumMessageBytes = 4096;
    private const int ProtocolVersion = 6;

    private static readonly HashSet<string> ResponseFields =
        ["protocolVersion", "requestId", "action", "payload"];
    private static readonly HashSet<string> CapabilityFields =
        ["supported", "reasonCode", "detail"];
    private static readonly HashSet<string> OperationFields =
    [
        "operationId",
        "sourceId",
        "displayName",
        "phase",
        "reasonCode",
        "detail",
        "createdAt",
        "updatedAt",
    ];
    private static readonly HashSet<string> ErrorFields =
        ["requestAction", "operationId", "code", "detail"];
    private static readonly Regex SourceIdPattern = new(
        "^[a-z0-9][a-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex UtcTimestampPattern = new(
        "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]{1,6})?Z$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly IReadOnlyDictionary<string, (bool Supported, string Detail)>
        Capabilities = new Dictionary<string, (bool, string)>(StringComparer.Ordinal)
        {
            ["supported"] = (true, "Source management is available."),
            ["installer_upgrade_required"] =
                (false, "Source management requires the latest installer."),
            ["unsupported_deployment"] =
                (false, "Source management is unavailable on this installation."),
            ["unsupported_platform"] =
                (false, "Source management is unavailable on this platform."),
        };

    private static readonly IReadOnlyDictionary<string, (string Reason, string Detail)>
        Operations = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["accepted"] = ("accepted", "Source change accepted."),
            ["validating"] = ("in_progress", "The source change is being validated."),
            ["applying"] = ("in_progress", "The source configuration is being applied."),
            ["restarting"] = ("in_progress", "ReachCommander is restarting."),
            ["healthChecking"] = ("in_progress", "ReachCommander is being checked."),
            ["completed"] = ("completed", "The source change was completed."),
            ["rolledBack"] = ("rolled_back", "The source change was rolled back."),
        };

    private static readonly IReadOnlyDictionary<string, string> ErrorDetails =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["invalid_request"] = "The source-management request is invalid.",
            ["request_too_large"] = "The source-management request is too large.",
            ["protocol_incompatible"] = "The source-management host protocol is incompatible.",
            ["invalid_action"] = "The source-management action is not supported.",
            ["unsupported"] = "Source management is unavailable on this installation.",
            ["busy"] = "Another source-management operation is in progress.",
            ["validation_failed"] = "The source folder could not be accepted.",
            ["untrusted_source_ancestry"] =
                "The source folder's parent directories must be root-owned and not group- or world-writable.",
            ["source_management_failed"] =
                "The source-management operation could not be completed.",
        };

    public async Task<SourceManagementCapability> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(
                    "status",
                    operationId: null,
                    (requestId, _) => JsonSerializer.Serialize(new
                    {
                        protocolVersion = ProtocolVersion,
                        requestId,
                        action = "status",
                    }),
                ParseCapability,
                mutation: false,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SourceManagementProtocolVersionException)
        {
            return new SourceManagementCapability(
                false,
                "installer_upgrade_required",
                "Source management requires the latest installer.");
        }
    }

    public async Task<SourceManagementOperation> AddAsync(
        SourceAddRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(
                "addSource",
                operationId: null,
                (requestId, _) => JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolVersion,
                    requestId,
                    action = "addSource",
                    displayName = request.DisplayName.Trim(),
                    hostPath = request.HostPath,
                    access = request.Access switch
                    {
                        SourceAccess.ReadOnly => "readOnly",
                        SourceAccess.ReadWrite => "readWrite",
                        _ => throw new SourceManagementValidationException(),
                    },
                }),
                ParseOperation,
                mutation: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceManagementProtocolVersionException)
        {
            throw new SourceManagementMutationOutcomeUnknownException();
        }
    }

    public async Task<SourceManagementOperation> RemoveAsync(
        string sourceId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(sourceId) || !SourceIdPattern.IsMatch(sourceId))
        {
            throw new SourceManagementValidationException();
        }

        try
        {
            return await SendAsync(
                "removeSource",
                operationId: null,
                (requestId, _) => JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolVersion,
                    requestId,
                    action = "removeSource",
                    sourceId,
                }),
                ParseOperation,
                mutation: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceManagementProtocolVersionException)
        {
            throw new SourceManagementMutationOutcomeUnknownException();
        }
    }

    public async Task<SourceManagementOperation> GetOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(
                "getOperation",
                operationId,
                (requestId, expectedOperationId) => JsonSerializer.Serialize(new
                {
                    protocolVersion = ProtocolVersion,
                    requestId,
                    action = "getOperation",
                    operationId = expectedOperationId,
                }),
                ParseOperation,
                mutation: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SourceManagementProtocolVersionException)
        {
            throw new SourceManagementProtocolIncompatibleException();
        }
    }

    private async Task<T> SendAsync<T>(
        string expectedAction,
        Guid? operationId,
        Func<Guid, Guid?, string> createRequest,
        Func<JsonElement, T> parsePayload,
        bool mutation,
        CancellationToken cancellationToken)
    {
        var requestId = requestIds.NewId();
        if (requestId == Guid.Empty)
        {
            throw new SourceManagementFailedException();
        }

        var request = createRequest(requestId, operationId) + "\n";
        if (Encoding.UTF8.GetByteCount(request) > MaximumMessageBytes)
        {
            throw new SourceManagementValidationException();
        }

        string response;
        try
        {
            response = await transport.ExchangeAsync(
                    request,
                    MaximumMessageBytes,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SystemUpdaterProtocolException)
        {
            throw mutation
                ? new SourceManagementMutationOutcomeUnknownException()
                : new SourceManagementFailedException();
        }
        catch (SystemUpdaterUnavailableException exception)
        {
            if (mutation && exception.RequestMayHaveBeenAccepted)
            {
                throw new SourceManagementMutationOutcomeUnknownException();
            }

            throw new SourceManagementUnavailableException();
        }
        catch
        {
            throw mutation
                ? new SourceManagementMutationOutcomeUnknownException()
                : new SourceManagementFailedException();
        }

        if (Encoding.UTF8.GetByteCount(response) > MaximumMessageBytes)
        {
            throw mutation
                ? new SourceManagementMutationOutcomeUnknownException()
                : new SourceManagementFailedException();
        }

        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            var root = document.RootElement;
            ValidateProtocolVersion(root);
            ValidateFields(root, ResponseFields);
            RequireCorrelatedId(root, "requestId", requestId);

            var action = RequiredString(root, "action", 20);
            var payload = root.GetProperty("payload");
            if (action == "error")
            {
                throw new DefiniteHostErrorException(
                    MapError(payload, expectedAction, operationId));
            }

            if (!string.Equals(action, expectedAction, StringComparison.Ordinal))
            {
                throw new SourceManagementProtocolIncompatibleException();
            }

            var result = parsePayload(payload);
            if (result is SourceManagementOperation operation &&
                operationId is { } expectedOperationId &&
                operation.OperationId != expectedOperationId)
            {
                throw new SourceManagementProtocolIncompatibleException();
            }

            return result;
        }
        catch (DefiniteHostErrorException exception)
        {
            throw exception.Error;
        }
        catch (SourceManagementException) when (mutation)
        {
            throw new SourceManagementMutationOutcomeUnknownException();
        }
        catch (SourceManagementException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw mutation
                ? new SourceManagementMutationOutcomeUnknownException()
                : new SourceManagementFailedException();
        }
        catch (InvalidOperationException)
        {
            throw mutation
                ? new SourceManagementMutationOutcomeUnknownException()
                : new SourceManagementFailedException();
        }
        catch (FormatException)
        {
            throw mutation
                ? new SourceManagementMutationOutcomeUnknownException()
                : new SourceManagementFailedException();
        }
    }

    private static SourceManagementCapability ParseCapability(JsonElement payload)
    {
        ValidateFields(payload, CapabilityFields);
        var supported = RequiredBoolean(payload, "supported");
        var reasonCode = RequiredString(payload, "reasonCode", 40);
        var detail = RequiredString(payload, "detail", 240);
        if (!Capabilities.TryGetValue(reasonCode, out var expected) ||
            supported != expected.Supported ||
            !string.Equals(detail, expected.Detail, StringComparison.Ordinal))
        {
            throw new SourceManagementFailedException();
        }

        return new SourceManagementCapability(supported, reasonCode, detail);
    }

    private static SourceManagementOperation ParseOperation(JsonElement payload)
    {
        ValidateFields(payload, OperationFields);
        var operationId = RequiredCanonicalGuid(payload, "operationId");
        var phaseName = RequiredString(payload, "phase", 30);
        var reasonCode = RequiredString(payload, "reasonCode", 40);
        var detail = RequiredString(payload, "detail", 240);
        var sourceId = OptionalString(payload, "sourceId", 64);
        var displayName = OptionalString(payload, "displayName", 80);
        if ((sourceId is null) != (displayName is null) ||
            (sourceId is not null && !SourceIdPattern.IsMatch(sourceId)))
        {
            throw new SourceManagementFailedException();
        }

        SourceManagementPhase phase;
        if (phaseName == "failed")
        {
            var expectedDetail = reasonCode switch
            {
                "validation_failed" or "source_management_failed" =>
                    "The source-management operation could not be completed.",
                "untrusted_source_ancestry" =>
                    "The source folder's parent directories must be root-owned and not group- or world-writable.",
                _ => null,
            };
            if (expectedDetail is null || detail != expectedDetail)
            {
                throw new SourceManagementFailedException();
            }

            phase = SourceManagementPhase.Failed;
        }
        else
        {
            if (!Operations.TryGetValue(phaseName, out var expected) ||
                reasonCode != expected.Reason ||
                detail != expected.Detail)
            {
                throw new SourceManagementFailedException();
            }

            phase = phaseName switch
            {
                "accepted" => SourceManagementPhase.Accepted,
                "validating" => SourceManagementPhase.Validating,
                "applying" => SourceManagementPhase.Applying,
                "restarting" => SourceManagementPhase.Restarting,
                "healthChecking" => SourceManagementPhase.HealthChecking,
                "completed" => SourceManagementPhase.Completed,
                "rolledBack" => SourceManagementPhase.RolledBack,
                _ => throw new SourceManagementFailedException(),
            };
        }

        if (phase == SourceManagementPhase.Completed && sourceId is null)
        {
            throw new SourceManagementFailedException();
        }

        var createdAt = RequiredUtcTimestamp(payload, "createdAt");
        var updatedAt = RequiredUtcTimestamp(payload, "updatedAt");
        if (updatedAt < createdAt)
        {
            throw new SourceManagementFailedException();
        }

        return new SourceManagementOperation(
            operationId,
            sourceId,
            displayName,
            phase,
            reasonCode,
            detail,
            createdAt,
            updatedAt);
    }

    private static SourceManagementException MapError(
        JsonElement payload,
        string expectedAction,
        Guid? expectedOperationId)
    {
        ValidateFields(payload, ErrorFields);
        var requestAction = RequiredString(payload, "requestAction", 20);
        if (requestAction != expectedAction)
        {
            throw new SourceManagementProtocolIncompatibleException();
        }

        var operationId = OptionalCanonicalGuid(payload, "operationId");
        if (requestAction == "getOperation")
        {
            if (operationId is null || operationId != expectedOperationId)
            {
                throw new SourceManagementProtocolIncompatibleException();
            }
        }
        else if (operationId is not null)
        {
            throw new SourceManagementProtocolIncompatibleException();
        }

        var code = RequiredString(payload, "code", 40);
        var detail = RequiredString(payload, "detail", 240);
        if (!ErrorDetails.TryGetValue(code, out var expectedDetail) || detail != expectedDetail)
        {
            throw new SourceManagementFailedException();
        }

        return code switch
        {
            "protocol_incompatible" => new SourceManagementProtocolIncompatibleException(),
            "unsupported" => new SourceManagementUnavailableException(),
            "busy" => new SourceManagementBusyException(),
            "validation_failed" => new SourceManagementValidationException(),
            "untrusted_source_ancestry" => new SourceManagementAncestryUntrustedException(),
            _ => new SourceManagementFailedException(),
        };
    }

    private sealed class DefiniteHostErrorException(SourceManagementException error)
        : Exception
    {
        public SourceManagementException Error { get; } = error;
    }

    private static void ValidateProtocolVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SourceManagementFailedException();
        }

        var versions = root.EnumerateObject()
            .Where(property => property.NameEquals("protocolVersion"))
            .ToArray();
        if (versions.Length != 1 ||
            versions[0].Value.ValueKind != JsonValueKind.Number ||
            !versions[0].Value.TryGetInt32(out var version))
        {
            throw new SourceManagementFailedException();
        }

        if (version != ProtocolVersion)
        {
            throw new SourceManagementProtocolVersionException();
        }
    }

    private static void ValidateFields(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new SourceManagementFailedException();
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !actual.Add(property.Name))
            {
                throw new SourceManagementFailedException();
            }
        }

        if (!expected.SetEquals(actual))
        {
            throw new SourceManagementFailedException();
        }
    }

    private static void RequireCorrelatedId(JsonElement root, string name, Guid expected)
    {
        if (RequiredCanonicalGuid(root, name) != expected)
        {
            throw new SourceManagementProtocolIncompatibleException();
        }
    }

    private static Guid RequiredCanonicalGuid(JsonElement root, string name) =>
        OptionalCanonicalGuid(root, name) ?? throw new SourceManagementFailedException();

    private static Guid? OptionalCanonicalGuid(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (raw is null ||
            !Guid.TryParseExact(raw, "D", out var parsed) ||
            raw != parsed.ToString("D"))
        {
            throw new SourceManagementFailedException();
        }

        return parsed;
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SourceManagementFailedException();
        }

        return value.GetBoolean();
    }

    private static string RequiredString(JsonElement root, string name, int maximumLength) =>
        OptionalString(root, name, maximumLength) ?? throw new SourceManagementFailedException();

    private static string? OptionalString(JsonElement root, string name, int maximumLength)
    {
        var value = root.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(text) ||
            text.Length > maximumLength ||
            text.Any(char.IsControl))
        {
            throw new SourceManagementFailedException();
        }

        return text;
    }

    private static DateTimeOffset RequiredUtcTimestamp(JsonElement root, string name)
    {
        var raw = RequiredString(root, name, 40);
        if (!UtcTimestampPattern.IsMatch(raw) ||
            !DateTimeOffset.TryParseExact(
                raw,
                "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new SourceManagementFailedException();
        }

        return timestamp;
    }

    private sealed class SourceManagementProtocolVersionException : Exception;
}
