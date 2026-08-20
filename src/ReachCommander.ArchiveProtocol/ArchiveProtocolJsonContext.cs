using System.Text.Json.Serialization;

namespace ReachCommander.ArchiveProtocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ArchiveWorkerLimits))]
[JsonSerializable(typeof(ArchiveInspectionRequest))]
[JsonSerializable(typeof(ArchiveExtractionRequest))]
[JsonSerializable(typeof(ArchiveDetectedFrame))]
[JsonSerializable(typeof(ArchiveEntryFrame))]
[JsonSerializable(typeof(ArchiveEntryStartFrame))]
[JsonSerializable(typeof(ArchiveEntryEndFrame))]
[JsonSerializable(typeof(ArchiveProgressFrame))]
[JsonSerializable(typeof(ArchiveCompletedFrame))]
[JsonSerializable(typeof(ArchiveFailureFrame))]
public sealed partial class ArchiveProtocolJsonContext : JsonSerializerContext;
