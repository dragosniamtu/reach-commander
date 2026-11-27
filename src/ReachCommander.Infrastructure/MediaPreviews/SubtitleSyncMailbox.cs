using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Application.MediaPreviews;
using ReachCommander.Infrastructure.FileOperations.Persistence;
using ReachCommander.Infrastructure.SubtitleGeneration;

namespace ReachCommander.Infrastructure.MediaPreviews;

internal sealed record SubtitleSyncResult(long OffsetMilliseconds, double TimeScale);

internal interface ISubtitleSyncEngine
{
    Task<SubtitleRepairCapabilities> GetCapabilitiesAsync(CancellationToken token);
    Task<SubtitleSyncResult> SynchronizeAsync(string videoPath, string subtitleSrt,
        Func<SubtitleRepairStatus, Task> progress, CancellationToken token);
    Task RecoverAsync(CancellationToken token);
}

internal sealed class SubtitleSyncMailbox(IOptions<SubtitleGenerationOptions> options) : ISubtitleSyncEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string Root => Path.GetFullPath(options.Value.SharedPath);
    private string Requests => Path.Combine(Root, "repair-requests");
    private string Results => Path.Combine(Root, "repair-results");

    public async Task<SubtitleRepairCapabilities> GetCapabilitiesAsync(CancellationToken token)
    {
        if (!options.Value.Enabled)
            return new(false, false, "subtitle_repair_disabled", "Enable the local subtitle worker to use automatic timing correction.");
        try
        {
            if (!Path.IsPathFullyQualified(options.Value.SharedPath)) return Unavailable();
            EnsureSafe(Root);
            var bytes = await ReadAsync(Path.Combine(Root, "repair-status.json"), 4096, token);
            if (bytes is null) return Unavailable();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var value = document.RootElement;
            var names = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 3 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(
                    ["version", "updatedAt", "synchronizationAvailable"]) ||
                value.GetProperty("version").GetInt32() != 1 ||
                !value.GetProperty("updatedAt").TryGetDateTimeOffset(out var updated) || updated.Offset != TimeSpan.Zero ||
                DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(30) ||
                updated - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(5) ||
                value.GetProperty("synchronizationAvailable").ValueKind != JsonValueKind.True)
                return Unavailable();
            return new(true, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidOperationException or FormatException or ArgumentException or MediaPreviewException)
        { return Unavailable(); }
    }

    public async Task<SubtitleSyncResult> SynchronizeAsync(string videoPath, string subtitleSrt,
        Func<SubtitleRepairStatus, Task> progress, CancellationToken token)
    {
        var capabilities = await GetCapabilitiesAsync(token);
        if (!capabilities.Available) throw new MediaPreviewException(capabilities.ReasonCode!, capabilities.Detail!);
        EnsureDirectories();
        if (Directory.EnumerateFiles(Requests, "*.json").Take(options.Value.MaximumQueuedJobs).Count() >= options.Value.MaximumQueuedJobs)
            throw new MediaPreviewException("subtitle_repair_busy", "The subtitle repair queue is full.");
        var id = Guid.NewGuid().ToString("N");
        await AtomicJsonFile.WriteAsync(Path.Combine(Requests, id + ".json"),
            new { version = 2, id, kind = "synchronize", videoPath, subtitleSrt }, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.Value.WorkerTimeout);
        var terminal = false;
        long? unavailableSince = null;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var result = await ReadResultAsync(id, timeout.Token);
                if (result is not null)
                {
                    if (result.State is "completed" or "failed" or "cancelled")
                    {
                        terminal = true;
                        if (result.State != "completed") throw Failure(result.ErrorCode);
                        if (result.OffsetMilliseconds is not { } offset || Math.Abs((double)offset) > 600000 ||
                            result.TimeScale is not { } scale || !double.IsFinite(scale) || scale is < 0.8 or > 1.25)
                            throw Failure("invalid_result");
                        return new(offset, scale);
                    }
                    await progress(new("running", result.Stage switch
                    {
                        "probing" => "Inspecting audio", "extracting" => "Extracting audio",
                        "synchronizing" => "Matching subtitle timing", _ => "Waiting for subtitle worker",
                    }, result.Percent));
                }
                if ((await GetCapabilitiesAsync(timeout.Token)).Available) unavailableSince = null;
                else
                {
                    unavailableSince ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(unavailableSince.Value) >= options.Value.WorkerUnavailableGrace)
                        throw Failure("alignment_unavailable");
                }
                await Task.Delay(options.Value.PollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw Failure("timeout"); }
        finally
        {
            if (!terminal)
            {
                await CancelRequestAsync(id);
                using var acknowledgement = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    while (!acknowledgement.IsCancellationRequested)
                    {
                        var result = await ReadResultAsync(id, acknowledgement.Token);
                        if (result?.State is "completed" or "failed" or "cancelled") { terminal = true; break; }
                        await Task.Delay(options.Value.PollInterval, acknowledgement.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or MediaPreviewException) { }
            }
            if (terminal) Clean(id);
        }
    }

    public async Task RecoverAsync(CancellationToken token)
    {
        if (!options.Value.Enabled) return;
        EnsureDirectories();
        foreach (var path in Directory.EnumerateFiles(Requests, "*.json"))
        {
            token.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            await CancelRequestAsync(id);
            try
            {
                var result = await ReadResultAsync(id, token);
                if (result?.State is "completed" or "failed" or "cancelled") Clean(id);
            }
            catch (MediaPreviewException) { }
        }
    }

    private async Task<WorkerResult?> ReadResultAsync(string id, CancellationToken token)
    {
        var bytes = await ReadAsync(Path.Combine(Results, id + ".json"), 16384, token);
        if (bytes is null) return null;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object) throw Failure("invalid_result");
            var names = value.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != names.Distinct(StringComparer.Ordinal).Count()) throw Failure("invalid_result");
            var result = JsonSerializer.Deserialize<WorkerResult>(bytes, JsonOptions);
            if (result is null || result.Version != 1 || result.Id != id || result.Percent is < 0 or > 100 ||
                result.State is not ("running" or "completed" or "failed" or "cancelled")) throw Failure("invalid_result");
            return result;
        }
        catch (JsonException) { throw Failure("invalid_result"); }
    }

    private static async Task<byte[]?> ReadAsync(string path, int maximum, CancellationToken token)
    {
        EnsureSafe(path);
        if (!File.Exists(path)) return null;
        try { return await MediaPreviewService.ReadBoundedSubtitleAsync(path, maximum, token); }
        catch (FileNotFoundException) { return null; }
    }

    private async Task CancelRequestAsync(string id)
    {
        EnsureDirectories();
        var path = Path.Combine(Requests, id + ".cancel");
        EnsureSafe(path);
        if (File.Exists(path)) return;
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await stream.FlushAsync();
        stream.Flush(true);
    }

    private void Clean(string id)
    {
        foreach (var path in new[] { Path.Combine(Requests, id + ".json"), Path.Combine(Results, id + ".json"), Path.Combine(Requests, id + ".cancel") })
        { EnsureSafe(path); File.Delete(path); }
    }

    private void EnsureDirectories()
    {
        foreach (var path in new[] { Root, Requests, Results }) { EnsureSafe(path); Directory.CreateDirectory(path); }
    }

    private static void EnsureSafe(string path)
    {
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))) throw Failure("path_denied");
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null || (dir.Exists && dir.Attributes.HasFlag(FileAttributes.ReparsePoint))) throw Failure("path_denied");
    }

    private static SubtitleRepairCapabilities Unavailable() => new(true, false, "subtitle_repair_unavailable",
        "Automatic timing needs the updated local subtitle worker. Start or update it, then recheck.");

    internal static MediaPreviewException Failure(string? code) => code switch
    {
        "no_audio" => new("subtitle_no_audio", "This video has no audio to match."),
        "alignment_unavailable" => new("subtitle_repair_unavailable", "The subtitle synchronization worker is unavailable."),
        "timeout" or "media_too_long" => new("subtitle_repair_limit", "The subtitle repair exceeded its time or duration limit."),
        _ => new("subtitle_repair_failed", "Automatic timing could not find a usable correction. The original subtitle is unchanged."),
    };

    private sealed record WorkerResult(int Version, string Id, string State, string? Stage, int Percent,
        long? OffsetMilliseconds, double? TimeScale, string? ErrorCode);
}
