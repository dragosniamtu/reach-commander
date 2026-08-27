using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Infrastructure.SystemUpdates;

internal interface ISystemUpdateDiagnosticsGateway
{
    Task<SystemUpdateDiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken);
}

internal sealed class SystemUpdateDiagnosticsGateway(
    ISystemUpdaterTransport transport,
    ISystemUpdateRequestIdGenerator requestIds) : ISystemUpdateDiagnosticsGateway
{
    internal const int ProtocolVersion = 4;
    internal const int MaximumResponseBytes = 262_144;
    private const int SchemaVersion = 1;

    private static readonly HashSet<string> ResponseFields =
        ["protocolVersion", "requestId", "diagnostics"];

    private static readonly HashSet<string> DiagnosticFields =
    [
        "schemaVersion",
        "generatedAt",
        "complete",
        "updaterProtocolVersion",
        "channel",
        "currentVersion",
        "operationId",
        "trace",
        "checks",
    ];

    private static readonly HashSet<string> CheckFields =
        ["name", "status", "reasonCode"];

    private static readonly IReadOnlyDictionary<string, SystemUpdateDiagnosticStatus> Statuses =
        new Dictionary<string, SystemUpdateDiagnosticStatus>(StringComparer.Ordinal)
        {
            ["healthy"] = SystemUpdateDiagnosticStatus.Healthy,
            ["warning"] = SystemUpdateDiagnosticStatus.Warning,
            ["failed"] = SystemUpdateDiagnosticStatus.Failed,
            ["timedOut"] = SystemUpdateDiagnosticStatus.TimedOut,
            ["unavailable"] = SystemUpdateDiagnosticStatus.Unavailable,
            ["notApplicable"] = SystemUpdateDiagnosticStatus.NotApplicable,
        };

    private static readonly Regex PinnedVersion = new(
        @"^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[A-Za-z-][0-9A-Za-z-]*))*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex EdgeVersion = new(
        "^edge@[0-9a-f]{12}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<SystemUpdateDiagnosticsSnapshot> CollectAsync(
        CancellationToken cancellationToken)
    {
        var requestId = requestIds.NewId();
        var request = JsonSerializer.Serialize(new
        {
            protocolVersion = ProtocolVersion,
            requestId,
            action = "collectDiagnostics",
        }) + "\n";
        var response = await transport.ExchangeAsync(
                request,
                MaximumResponseBytes,
                cancellationToken)
            .ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(response) > MaximumResponseBytes)
        {
            throw ProtocolError();
        }

        return Parse(response, requestId);
    }

    private static SystemUpdateDiagnosticsSnapshot Parse(string response, Guid expectedRequestId)
    {
        try
        {
            using var document = JsonDocument.Parse(response, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            var root = document.RootElement;
            ValidateFields(root, ResponseFields);
            if (RequiredInt(root, "protocolVersion") != ProtocolVersion ||
                !Guid.TryParseExact(RequiredString(root, "requestId"), "D", out var responseId) ||
                responseId != expectedRequestId)
            {
                throw ProtocolError();
            }

            var diagnostics = root.GetProperty("diagnostics");
            ValidateFields(diagnostics, DiagnosticFields);
            if (RequiredInt(diagnostics, "schemaVersion") != SchemaVersion ||
                RequiredInt(diagnostics, "updaterProtocolVersion") != ProtocolVersion)
            {
                throw ProtocolError();
            }

            var generatedAt = RequiredTimestamp(diagnostics, "generatedAt");
            var complete = RequiredBoolean(diagnostics, "complete");
            var operationId = OptionalLogicalString(diagnostics, "operationId", requireGuid: true);
            var checks = ParseChecks(diagnostics.GetProperty("checks"));
            var isComplete = checks.All(check => check.Status is not (
                SystemUpdateDiagnosticStatus.TimedOut or SystemUpdateDiagnosticStatus.Unavailable));
            if (complete != isComplete)
            {
                throw ProtocolError();
            }

            return new SystemUpdateDiagnosticsSnapshot(
                SchemaVersion,
                generatedAt,
                complete,
                ProtocolVersion,
                OptionalChannel(diagnostics),
                OptionalDisplayVersion(diagnostics),
                operationId,
                SystemUpdaterGateway.ParseDiagnosticTrace(diagnostics, operationId),
                checks);
        }
        catch (SystemUpdaterProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw ProtocolError();
        }
        catch (InvalidOperationException)
        {
            throw ProtocolError();
        }
        catch (KeyNotFoundException)
        {
            throw ProtocolError();
        }
    }

    private static IReadOnlyList<SystemUpdateDiagnosticCheck> ParseChecks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() != SystemUpdateDiagnostics.CheckNames.Count)
        {
            throw ProtocolError();
        }

        var checks = new List<SystemUpdateDiagnosticCheck>(element.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in element.EnumerateArray())
        {
            ValidateFields(value, CheckFields);
            var name = RequiredString(value, "name");
            var statusName = RequiredString(value, "status");
            var reasonCode = RequiredString(value, "reasonCode");
            if (!SystemUpdateDiagnostics.CheckNames.Contains(name, StringComparer.Ordinal) ||
                !seen.Add(name) ||
                !Statuses.TryGetValue(statusName, out var status) ||
                reasonCode != SystemUpdateDiagnostics.ReasonCode(name, status))
            {
                throw ProtocolError();
            }

            checks.Add(new SystemUpdateDiagnosticCheck(name, status, reasonCode));
        }

        if (!SystemUpdateDiagnostics.CheckNames.All(seen.Contains))
        {
            throw ProtocolError();
        }

        return checks;
    }

    private static void ValidateFields(JsonElement element, HashSet<string> expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw ProtocolError();
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !actual.Add(property.Name))
            {
                throw ProtocolError();
            }
        }

        if (!actual.SetEquals(expected))
        {
            throw ProtocolError();
        }
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
        {
            throw ProtocolError();
        }

        return result;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw ProtocolError();
        }

        return value.GetBoolean();
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        var result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(result) || result.Length > 96 ||
            result.Any(character => character is '\r' or '\n'))
        {
            throw ProtocolError();
        }

        return result;
    }

    private static string? OptionalLogicalString(
        JsonElement element,
        string name,
        bool requireGuid = false)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var result = RequiredString(element, name);
        if (requireGuid)
        {
            if (!Guid.TryParseExact(result, "D", out _))
            {
                throw ProtocolError();
            }
        }
        else if (result.Any(character =>
                     !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_' or '@')))
        {
            throw ProtocolError();
        }

        return result;
    }

    private static string? OptionalChannel(JsonElement element)
    {
        var value = OptionalLogicalString(element, "channel");
        if (value is not null && value is not ("stable" or "edge") && !PinnedVersion.IsMatch(value))
        {
            throw ProtocolError();
        }

        return value;
    }

    private static string? OptionalDisplayVersion(JsonElement element)
    {
        var value = OptionalLogicalString(element, "currentVersion");
        if (value is not null && value != "unknown" &&
            !PinnedVersion.IsMatch(value) && !EdgeVersion.IsMatch(value))
        {
            throw ProtocolError();
        }

        return value;
    }

    private static DateTimeOffset RequiredTimestamp(JsonElement element, string name)
    {
        var raw = RequiredString(element, name);
        if (!raw.EndsWith('Z') ||
            !DateTimeOffset.TryParseExact(
                raw,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value))
        {
            throw ProtocolError();
        }

        return value;
    }

    private static SystemUpdaterProtocolException ProtocolError() =>
        new("The updater diagnostics response is incompatible.");
}

internal sealed class UnavailableSystemUpdateDiagnosticsGateway : ISystemUpdateDiagnosticsGateway
{
    public Task<SystemUpdateDiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken) =>
        throw new SystemUpdaterUnavailableException("Host diagnostics are unavailable.");
}
