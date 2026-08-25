using System.Globalization;
using System.Text.Json;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal sealed record UpdaterSnapshot(
    int ProtocolVersion,
    bool Supported,
    string? Channel,
    string? CurrentVersion,
    string? TargetVersion,
    string Phase,
    string ReasonCode,
    string Detail,
    string? OperationId,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset UpdatedAt);

internal interface ISystemUpdaterGateway
{
    Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken);

    Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken);
}

internal interface ISystemUpdateRequestIdGenerator
{
    Guid NewId();
}

internal sealed class SystemUpdateRequestIdGenerator : ISystemUpdateRequestIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

internal sealed class SystemUpdaterProtocolException(string message)
    : Exception(message)
{
    public string Code { get; } = "system_update_protocol_incompatible";
}

internal sealed class SystemUpdaterUnavailableException(string message)
    : Exception(message);

internal sealed class SystemUpdaterGateway(
    ISystemUpdaterTransport transport,
    ISystemUpdateRequestIdGenerator requestIds) : ISystemUpdaterGateway
{
    public const int MaximumMessageBytes = 65_536;

    private static readonly HashSet<string> ResponseFields =
    [
        "protocolVersion",
        "requestId",
        "supported",
        "channel",
        "currentVersion",
        "targetVersion",
        "currentDigest",
        "targetDigest",
        "phase",
        "reasonCode",
        "detail",
        "operationId",
        "lastCheckedAt",
        "updatedAt",
    ];

    private static readonly HashSet<string> Phases =
    [
        "unavailable",
        "current",
        "available",
        "applying",
        "completed",
        "rolledBack",
        "failed",
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> ReasonsByPhase =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["unavailable"] =
            [
                "version_pinned",
                "invalid_state",
                "release_unavailable",
                "release_invalid",
                "manifest_unavailable",
                "manifest_invalid",
                "request_timeout",
                "request_too_large",
                "invalid_request",
                "invalid_action",
                "protocol_incompatible",
                "response_too_large",
            ],
            ["current"] = ["up_to_date"],
            ["available"] = ["update_available"],
            ["applying"] = ["update_applying"],
            ["completed"] = ["update_completed"],
            ["rolledBack"] = ["candidate_rolled_back"],
            ["failed"] = ["update_failed", "update_interrupted", "updater_journal_invalid"],
        };

    public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken) =>
        SendAsync("check", cancellationToken);

    public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
        SendAsync("applyConfiguredChannel", cancellationToken);

    private async Task<UpdaterSnapshot> SendAsync(
        string action,
        CancellationToken cancellationToken)
    {
        var requestId = requestIds.NewId();
        var request = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            requestId,
            action,
        }) + "\n";
        var response = await transport.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (System.Text.Encoding.UTF8.GetByteCount(response) > MaximumMessageBytes)
        {
            throw new SystemUpdaterProtocolException("The updater response is too large.");
        }

        return Parse(response, requestId);
    }

    private static UpdaterSnapshot Parse(string response, Guid expectedRequestId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(response, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 8,
            });
        }
        catch (JsonException exception)
        {
            throw new SystemUpdaterProtocolException($"The updater returned invalid JSON: {exception.GetType().Name}.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new SystemUpdaterProtocolException("The updater response must be an object.");
            }

            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!ResponseFields.Contains(property.Name) || !fields.Add(property.Name))
                {
                    throw new SystemUpdaterProtocolException("The updater response schema is incompatible.");
                }
            }

            if (!ResponseFields.SetEquals(fields))
            {
                throw new SystemUpdaterProtocolException("The updater response is incomplete.");
            }

            var root = document.RootElement;
            var protocolVersion = RequiredInt(root, "protocolVersion");
            if (protocolVersion != 1)
            {
                throw new SystemUpdaterProtocolException("The updater protocol version is incompatible.");
            }

            var requestId = RequiredString(root, "requestId");
            if (!Guid.TryParseExact(requestId, "D", out var parsedRequestId) ||
                parsedRequestId != expectedRequestId)
            {
                throw new SystemUpdaterProtocolException("The updater response identifier does not match.");
            }

            var phase = RequiredString(root, "phase");
            if (!Phases.Contains(phase))
            {
                throw new SystemUpdaterProtocolException("The updater phase is incompatible.");
            }

            var reasonCode = RequiredLogicalString(root, "reasonCode");
            if (!ReasonsByPhase[phase].Contains(reasonCode))
            {
                throw new SystemUpdaterProtocolException("The updater phase and reason are incompatible.");
            }

            var updatedAt = RequiredTimestamp(root, "updatedAt");
            return new UpdaterSnapshot(
                protocolVersion,
                RequiredBoolean(root, "supported"),
                OptionalLogicalString(root, "channel"),
                OptionalLogicalString(root, "currentVersion"),
                OptionalLogicalString(root, "targetVersion"),
                phase,
                reasonCode,
                RequiredBoundedString(root, "detail"),
                OptionalLogicalString(root, "operationId"),
                OptionalTimestamp(root, "lastCheckedAt"),
                updatedAt);
        }
    }

    private static int RequiredInt(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (!property.TryGetInt32(out var value))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return value;
    }

    private static bool RequiredBoolean(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return property.GetBoolean();
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return value;
    }

    private static string RequiredLogicalString(JsonElement root, string name) =>
        OptionalLogicalString(root, name) ??
        throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");

    private static string RequiredBoundedString(JsonElement root, string name)
    {
        var value = RequiredString(root, name);
        if (value.Length > 240 || value.Contains('\r') || value.Contains('\n'))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return value;
    }

    private static string? OptionalLogicalString(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 80 ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '@')))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return value;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement root, string name) =>
        OptionalTimestamp(root, name) ??
        throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");

    private static DateTimeOffset? OptionalTimestamp(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var raw = property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        if (raw is null ||
            raw.Length > 40 ||
            !DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return timestamp;
    }
}

internal sealed class UnavailableSystemUpdaterGateway : ISystemUpdaterGateway
{
    private static UpdaterSnapshot Unsupported => new(
        1,
        false,
        null,
        null,
        null,
        "unavailable",
        "system_update_unavailable",
        "System updates are unavailable on this installation.",
        null,
        null,
        DateTimeOffset.UtcNow);

    public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Unsupported);

    public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Unsupported);
}
