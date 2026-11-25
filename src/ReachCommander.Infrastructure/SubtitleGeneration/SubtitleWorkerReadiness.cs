using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SubtitleGeneration;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed class SubtitleWorkerReadiness(IOptions<SubtitleGenerationOptions> options)
{
    internal static readonly TimeSpan MaximumStatusAge = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan FutureAllowance = TimeSpan.FromSeconds(5);

    public async Task<SubtitleGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
            return new(false, false, false, false, "subtitle_generation_disabled", "Subtitle generation is not enabled. Configure the local worker first.");
        try
        {
            if (!Path.IsPathFullyQualified(options.Value.SharedPath)) return Unavailable();
            var root = Path.GetFullPath(options.Value.SharedPath);
            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if (directory.LinkTarget is not null || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    return Unavailable();
            var file = new FileInfo(Path.Combine(root, "status.json"));
            if (!file.Exists || file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                file.Attributes.HasFlag(FileAttributes.Directory) || file.Length is <= 0 or > 4096) return Unavailable();
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[4097];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
            if (count > 4096) return Unavailable();
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 4 });
            var status = document.RootElement;
            if (status.ValueKind != JsonValueKind.Object) return Unavailable();
            var names = status.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Length != 4 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                ["version", "updatedAt", "englishAvailable", "romanianAvailable"])) return Unavailable();
            var timestamp = status.GetProperty("updatedAt").GetString();
            if (timestamp is null || !timestamp.Contains('T') ||
                !(timestamp.EndsWith('Z') || timestamp.EndsWith("+00:00", StringComparison.Ordinal))) return Unavailable();
            if (!status.GetProperty("version").TryGetInt32(out var version) || version != 1 ||
                !status.GetProperty("updatedAt").TryGetDateTimeOffset(out var updatedAt) || updatedAt.Offset != TimeSpan.Zero ||
                status.GetProperty("englishAvailable").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                status.GetProperty("romanianAvailable").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return Unavailable();
            var age = DateTimeOffset.UtcNow - updatedAt;
            if (age > MaximumStatusAge || age < -FutureAllowance) return Unavailable();
            var english = status.GetProperty("englishAvailable").GetBoolean();
            var romanian = status.GetProperty("romanianAvailable").GetBoolean();
            if (romanian && !english) return Unavailable();
            return new(true, true, english, romanian, english && romanian ? null : "subtitle_models_missing",
                english && romanian ? null : english ? "Download the Romanian translation model to generate Romanian subtitles."
                    : "Download the local subtitle models before generating subtitles.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or System.Security.SecurityException or NotSupportedException)
        {
            return Unavailable();
        }
    }

    internal async Task RequireAvailableAsync(bool romanian, CancellationToken token)
    {
        var readiness = await GetCapabilitiesAsync(token);
        if (!readiness.Enabled || !readiness.WorkerAvailable || !readiness.EnglishAvailable || (romanian && !readiness.RomanianAvailable))
            throw new SubtitleGenerationException(readiness.ReasonCode!, readiness.Detail!);
    }

    private static SubtitleGenerationCapabilities Unavailable() => new(true, false, false, false,
        "subtitle_worker_unavailable", "The local subtitle worker is unavailable. Start or repair it before retrying.");
}
