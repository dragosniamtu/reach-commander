using System.Globalization;
using System.Text.Json;
using ReachCommander.Application.SystemUpdates;

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
    DateTimeOffset UpdatedAt,
    string? ProgressStage = null,
    SystemUpdateTrace? Trace = null);

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
    private const int LegacyProtocolVersion = 1;
    private const int DetailedProtocolVersion = 2;
    private const int TraceProtocolVersion = 3;
    private const int MaximumPublicTraceEvents = 32;

    private static readonly HashSet<string> V1ResponseFields =
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

    private static readonly HashSet<string> V2ResponseFields =
        [.. V1ResponseFields, "progressStage"];

    private static readonly HashSet<string> V3ResponseFields =
        [.. V2ResponseFields, "trace"];

    private static readonly HashSet<string> TraceFields =
    [
        "startedAt",
        "elapsedSeconds",
        "lastActivityAt",
        "events",
    ];

    private static readonly HashSet<string> TraceEventFields =
    [
        "sequence",
        "timestamp",
        "elapsedSeconds",
        "code",
        "stage",
        "outcome",
    ];

    private static readonly HashSet<string> ProgressStages =
    [
        "downloading",
        "installing",
        "restarting",
        "healthChecking",
        "restoring",
        "restartingPrevious",
        "verifyingRecovery",
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

    private static readonly IReadOnlyDictionary<string, SystemUpdateTraceEventCode> TraceCodes =
        new Dictionary<string, SystemUpdateTraceEventCode>(StringComparer.Ordinal)
        {
            ["operationAccepted"] = SystemUpdateTraceEventCode.OperationAccepted,
            ["downloadStarted"] = SystemUpdateTraceEventCode.DownloadStarted,
            ["hostActivity"] = SystemUpdateTraceEventCode.HostActivity,
            ["downloadCompleted"] = SystemUpdateTraceEventCode.DownloadCompleted,
            ["backupStarted"] = SystemUpdateTraceEventCode.BackupStarted,
            ["backupCompleted"] = SystemUpdateTraceEventCode.BackupCompleted,
            ["installStarted"] = SystemUpdateTraceEventCode.InstallStarted,
            ["installCompleted"] = SystemUpdateTraceEventCode.InstallCompleted,
            ["candidateRestartStarted"] = SystemUpdateTraceEventCode.CandidateRestartStarted,
            ["candidateRestartCompleted"] = SystemUpdateTraceEventCode.CandidateRestartCompleted,
            ["candidateImageVerified"] = SystemUpdateTraceEventCode.CandidateImageVerified,
            ["candidateHealthStarted"] = SystemUpdateTraceEventCode.CandidateHealthStarted,
            ["candidateHealthActivity"] = SystemUpdateTraceEventCode.CandidateHealthActivity,
            ["candidateHealthSucceeded"] = SystemUpdateTraceEventCode.CandidateHealthSucceeded,
            ["candidateHealthFailed"] = SystemUpdateTraceEventCode.CandidateHealthFailed,
            ["rollbackStarted"] = SystemUpdateTraceEventCode.RollbackStarted,
            ["rollbackStateRestored"] = SystemUpdateTraceEventCode.RollbackStateRestored,
            ["previousRestartStarted"] = SystemUpdateTraceEventCode.PreviousRestartStarted,
            ["previousRestartCompleted"] = SystemUpdateTraceEventCode.PreviousRestartCompleted,
            ["previousImageVerified"] = SystemUpdateTraceEventCode.PreviousImageVerified,
            ["recoveryHealthStarted"] = SystemUpdateTraceEventCode.RecoveryHealthStarted,
            ["recoveryHealthActivity"] = SystemUpdateTraceEventCode.RecoveryHealthActivity,
            ["recoveryHealthSucceeded"] = SystemUpdateTraceEventCode.RecoveryHealthSucceeded,
            ["recoveryHealthFailed"] = SystemUpdateTraceEventCode.RecoveryHealthFailed,
            ["commandTimedOut"] = SystemUpdateTraceEventCode.CommandTimedOut,
            ["terminationRequested"] = SystemUpdateTraceEventCode.TerminationRequested,
            ["terminationForced"] = SystemUpdateTraceEventCode.TerminationForced,
            ["operationCompleted"] = SystemUpdateTraceEventCode.OperationCompleted,
            ["operationRolledBack"] = SystemUpdateTraceEventCode.OperationRolledBack,
            ["operationFailed"] = SystemUpdateTraceEventCode.OperationFailed,
        };

    private static readonly IReadOnlyDictionary<string, SystemUpdateTraceOutcome> TraceOutcomes =
        new Dictionary<string, SystemUpdateTraceOutcome>(StringComparer.Ordinal)
        {
            ["started"] = SystemUpdateTraceOutcome.Started,
            ["activity"] = SystemUpdateTraceOutcome.Activity,
            ["succeeded"] = SystemUpdateTraceOutcome.Succeeded,
            ["failed"] = SystemUpdateTraceOutcome.Failed,
            ["timedOut"] = SystemUpdateTraceOutcome.TimedOut,
        };

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
            ["failed"] = ["update_failed", "update_interrupted", "updater_journal_invalid", "update_command_timeout"],
        };

    public Task<UpdaterSnapshot> CheckAsync(CancellationToken cancellationToken) =>
        SendAsync("check", cancellationToken);

    public Task<UpdaterSnapshot> ApplyAsync(CancellationToken cancellationToken) =>
        SendAsync("applyConfiguredChannel", cancellationToken);

    private async Task<UpdaterSnapshot> SendAsync(
        string action,
        CancellationToken cancellationToken)
    {
        var traced = await ExchangeAsync(
                action,
                TraceProtocolVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsProtocolIncompatible(traced.Response, DetailedProtocolVersion) &&
            !IsProtocolIncompatible(traced.Response, LegacyProtocolVersion))
        {
            return Parse(
                traced.Response,
                traced.RequestId,
                TraceProtocolVersion);
        }

        var detailed = await ExchangeAsync(
                action,
                DetailedProtocolVersion,
                cancellationToken)
            .ConfigureAwait(false);
        if (!IsProtocolIncompatible(detailed.Response, LegacyProtocolVersion))
        {
            return Parse(
                detailed.Response,
                detailed.RequestId,
                DetailedProtocolVersion);
        }

        var legacy = await ExchangeAsync(action, LegacyProtocolVersion, cancellationToken)
            .ConfigureAwait(false);
        return Parse(legacy.Response, legacy.RequestId, LegacyProtocolVersion);
    }

    private async Task<(string Response, Guid RequestId)> ExchangeAsync(
        string action,
        int protocolVersion,
        CancellationToken cancellationToken)
    {
        var requestId = requestIds.NewId();
        var request = JsonSerializer.Serialize(new
        {
            protocolVersion,
            requestId,
            action,
        }) + "\n";
        var response = await transport.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (System.Text.Encoding.UTF8.GetByteCount(response) > MaximumMessageBytes)
        {
            throw new SystemUpdaterProtocolException("The updater response is too large.");
        }

        return (response, requestId);
    }

    private static UpdaterSnapshot Parse(
        string response,
        Guid expectedRequestId,
        int expectedProtocolVersion)
    {
        using var document = ParseDocument(response);
        var root = document.RootElement;
        var expectedFields = expectedProtocolVersion switch
        {
            LegacyProtocolVersion => V1ResponseFields,
            DetailedProtocolVersion => V2ResponseFields,
            TraceProtocolVersion => V3ResponseFields,
            _ => throw new SystemUpdaterProtocolException(
                "The updater protocol version is incompatible."),
        };
        ValidateFields(root, expectedFields);

        var protocolVersion = RequiredInt(root, "protocolVersion");
        if (protocolVersion != expectedProtocolVersion)
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

        var progressStage = expectedProtocolVersion >= DetailedProtocolVersion
            ? OptionalProgressStage(root)
            : null;
        if (progressStage is not null && phase is not (
                "applying" or "completed" or "rolledBack" or "failed"))
        {
            throw new SystemUpdaterProtocolException(
                "The updater phase and progress stage are incompatible.");
        }

        var operationId = OptionalLogicalString(root, "operationId");
        var trace = expectedProtocolVersion == TraceProtocolVersion
            ? OptionalTrace(root, phase, operationId)
            : null;
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
            operationId,
            OptionalTimestamp(root, "lastCheckedAt"),
            updatedAt,
            progressStage,
            trace);
    }

    private static SystemUpdateTrace? OptionalTrace(
        JsonElement root,
        string phase,
        string? operationId)
    {
        var property = root.GetProperty("trace");
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (phase is not ("applying" or "completed" or "rolledBack" or "failed") ||
            operationId is null)
        {
            throw new SystemUpdaterProtocolException(
                "The updater phase and trace are incompatible.");
        }

        ValidateFields(property, TraceFields);
        var startedAt = RequiredTimestamp(property, "startedAt");
        var elapsedSeconds = RequiredNonNegativeLong(property, "elapsedSeconds");
        var lastActivityAt = OptionalTimestamp(property, "lastActivityAt");
        if (lastActivityAt is { } activity && activity < startedAt)
        {
            throw new SystemUpdaterProtocolException("The updater trace activity is invalid.");
        }

        var eventsProperty = property.GetProperty("events");
        if (eventsProperty.ValueKind != JsonValueKind.Array ||
            eventsProperty.GetArrayLength() > MaximumPublicTraceEvents)
        {
            throw new SystemUpdaterProtocolException("The updater trace events are invalid.");
        }

        var events = new List<SystemUpdateTraceEvent>(eventsProperty.GetArrayLength());
        var previousSequence = 0;
        var previousElapsed = -1L;
        DateTimeOffset? previousTimestamp = null;
        foreach (var item in eventsProperty.EnumerateArray())
        {
            ValidateFields(item, TraceEventFields);
            var sequence = RequiredInt(item, "sequence");
            var timestamp = RequiredTimestamp(item, "timestamp");
            var eventElapsed = RequiredNonNegativeLong(item, "elapsedSeconds");
            if (sequence <= 0 ||
                sequence <= previousSequence ||
                eventElapsed < previousElapsed ||
                eventElapsed > elapsedSeconds ||
                timestamp < startedAt ||
                (previousTimestamp is not null && timestamp < previousTimestamp))
            {
                throw new SystemUpdaterProtocolException("The updater trace event order is invalid.");
            }

            var codeName = RequiredString(item, "code");
            var outcomeName = RequiredString(item, "outcome");
            if (!TraceCodes.TryGetValue(codeName, out var code) ||
                !TraceOutcomes.TryGetValue(outcomeName, out var outcome))
            {
                throw new SystemUpdaterProtocolException("The updater trace event is incompatible.");
            }

            events.Add(new SystemUpdateTraceEvent(
                sequence,
                timestamp,
                eventElapsed,
                code,
                MapProgressStage(OptionalProgressStage(item, "stage")),
                outcome));
            previousSequence = sequence;
            previousElapsed = eventElapsed;
            previousTimestamp = timestamp;
        }

        return new SystemUpdateTrace(
            startedAt,
            elapsedSeconds,
            lastActivityAt,
            events.ToArray());
    }

    private static JsonDocument ParseDocument(string response)
    {
        try
        {
            return JsonDocument.Parse(response, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false,
                MaxDepth = 8,
            });
        }
        catch (JsonException exception)
        {
            throw new SystemUpdaterProtocolException(
                $"The updater returned invalid JSON: {exception.GetType().Name}.");
        }
    }

    private static void ValidateFields(JsonElement root, HashSet<string> expectedFields)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new SystemUpdaterProtocolException("The updater response must be an object.");
        }

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!expectedFields.Contains(property.Name) || !fields.Add(property.Name))
            {
                throw new SystemUpdaterProtocolException("The updater response schema is incompatible.");
            }
        }

        if (!expectedFields.SetEquals(fields))
        {
            throw new SystemUpdaterProtocolException("The updater response is incomplete.");
        }
    }

    private static bool IsProtocolIncompatible(
        string response,
        int responseProtocolVersion)
    {
        try
        {
            using var document = ParseDocument(response);
            var root = document.RootElement;
            if (responseProtocolVersion is not (
                LegacyProtocolVersion or DetailedProtocolVersion))
            {
                throw new SystemUpdaterProtocolException(
                    "The updater protocol version is incompatible.");
            }

            // Historical helpers emitted compatibility errors with the v1 field set,
            // while preserving their own protocol version number.
            ValidateFields(root, V1ResponseFields);
            return RequiredInt(root, "protocolVersion") == responseProtocolVersion &&
                   root.GetProperty("requestId").ValueKind == JsonValueKind.Null &&
                   RequiredBoolean(root, "supported") &&
                   IsNull(root, "channel") &&
                   IsNull(root, "currentVersion") &&
                   IsNull(root, "targetVersion") &&
                   IsNull(root, "currentDigest") &&
                   IsNull(root, "targetDigest") &&
                   RequiredString(root, "phase") == "unavailable" &&
                   RequiredLogicalString(root, "reasonCode") == "protocol_incompatible" &&
                   RequiredBoundedString(root, "detail") ==
                   "The host updater protocol is incompatible." &&
                   IsNull(root, "operationId") &&
                   IsNull(root, "lastCheckedAt") &&
                   IsNull(root, "updatedAt");
        }
        catch (SystemUpdaterProtocolException)
        {
            return false;
        }
    }

    private static bool IsNull(JsonElement root, string name) =>
        root.GetProperty(name).ValueKind == JsonValueKind.Null;

    private static string? OptionalProgressStage(
        JsonElement root,
        string name = "progressStage")
    {
        var property = root.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var value = property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
        if (value is null || !ProgressStages.Contains(value))
        {
            throw new SystemUpdaterProtocolException(
                "The updater progress stage is incompatible.");
        }

        return value;
    }

    private static SystemUpdateProgressStage? MapProgressStage(string? stage) => stage switch
    {
        null => null,
        "downloading" => SystemUpdateProgressStage.Downloading,
        "installing" => SystemUpdateProgressStage.Installing,
        "restarting" => SystemUpdateProgressStage.Restarting,
        "healthChecking" => SystemUpdateProgressStage.HealthChecking,
        "restoring" => SystemUpdateProgressStage.Restoring,
        "restartingPrevious" => SystemUpdateProgressStage.RestartingPrevious,
        "verifyingRecovery" => SystemUpdateProgressStage.VerifyingRecovery,
        _ => throw new SystemUpdaterProtocolException(
            "The updater progress stage is incompatible."),
    };

    private static int RequiredInt(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt32(out var value))
        {
            throw new SystemUpdaterProtocolException($"The updater field '{name}' is invalid.");
        }

        return value;
    }

    private static long RequiredNonNegativeLong(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number ||
            !property.TryGetInt64(out var value) ||
            value < 0)
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
