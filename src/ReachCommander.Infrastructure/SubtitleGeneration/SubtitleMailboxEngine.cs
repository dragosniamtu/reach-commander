using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed class SubtitleMailboxEngine(IOptions<SubtitleGenerationOptions> options) : ISubtitleEngine
{
    private const int MaximumResultBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.GetFullPath(options.Value.SharedPath);
    private readonly SubtitleWorkerReadiness _readiness = new(options);
    private string Requests => Path.Combine(_root, "requests");
    private string Results => Path.Combine(_root, "results");

    public async Task<SubtitleEngineResult> GenerateAsync(SubtitleVideo video, bool romanian,
        Func<SubtitleEngineProgress, Task> progress, CancellationToken cancellationToken)
    {
        await _readiness.RequireAvailableAsync(romanian, cancellationToken);
        EnsureDirectories();
        await CancelAbandonedAsync(cancellationToken);
        if (Directory.EnumerateFiles(Requests, "*.json").Take(options.Value.MaximumQueuedJobs).Count() >= options.Value.MaximumQueuedJobs)
            throw new SubtitleGenerationException("subtitle_worker_unavailable", "The worker has not acknowledged earlier requests. Start or repair the local worker before retrying.");
        var id = Guid.NewGuid().ToString("N");
        var requestPath = Path.Combine(Requests, id + ".json");
        await AtomicJsonFile.WriteAsync(requestPath, new WorkerRequest(1, id, video.PhysicalPath, romanian), cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.WorkerTimeout);
        var terminal = false;
        SubtitleEngineProgress? previous = null;
        long? unavailableSince = null;
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var result = await ReadResultAsync(id, timeout.Token);
                if (result is not null)
                {
                    if (result.State == "completed")
                    {
                        terminal = true;
                        if (string.IsNullOrWhiteSpace(result.EnglishSrt) || (romanian && string.IsNullOrWhiteSpace(result.RomanianSrt)))
                            throw InvalidResult();
                        return new(result.EnglishSrt, result.RomanianSrt);
                    }
                    if (result.State is "failed" or "cancelled")
                    {
                        terminal = true;
                        throw WorkerError(result.ErrorCode);
                    }
                    var update = new SubtitleEngineProgress(SafeStage(result.Stage), result.Percent);
                    if (update != previous)
                    {
                        await progress(update);
                        previous = update;
                    }
                }
                var readiness = await _readiness.GetCapabilitiesAsync(timeout.Token);
                if (readiness.WorkerAvailable) unavailableSince = null;
                else
                {
                    unavailableSince ??= Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(unavailableSince.Value) >= options.Value.WorkerUnavailableGrace)
                        throw new SubtitleGenerationException("subtitle_worker_unavailable", "The local subtitle worker stopped responding. Start or repair it before retrying.");
                }
                await Task.Delay(options.Value.PollInterval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            throw new SubtitleGenerationException("subtitle_worker_timeout", "The local subtitle worker exceeded its time limit.");
        }
        finally
        {
            if (!terminal)
            {
                await MarkCancelledAsync(id);
                // Keep the durable cancellation marker if the worker is unavailable. On restart it must acknowledge before cleanup.
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
                catch (Exception exception) when (exception is OperationCanceledException or IOException or SubtitleGenerationException) { }
            }
            if (terminal) Clean(id);
        }
    }

    public async Task CancelAbandonedAsync(CancellationToken cancellationToken)
    {
        EnsureDirectories();
        foreach (var request in Directory.EnumerateFiles(Requests, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(request);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            await MarkCancelledAsync(id);
            try
            {
                var result = await ReadResultAsync(id, cancellationToken);
                if (result?.State is "completed" or "failed" or "cancelled") Clean(id);
            }
            catch (SubtitleGenerationException) { /* Do not trust or remove an invalid worker response. */ }
        }
    }

    private async Task<WorkerResult?> ReadResultAsync(string id, CancellationToken token)
    {
        EnsureDirectories();
        var path = Path.Combine(Results, id + ".json");
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaximumResultBytes)
            throw InvalidResult();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                32768, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var contents = new MemoryStream();
            var buffer = new byte[32768];
            int count;
            while ((count = await stream.ReadAsync(buffer, token)) != 0)
            {
                if (contents.Length + count > MaximumResultBytes) throw InvalidResult();
                contents.Write(buffer, 0, count);
            }
            var result = JsonSerializer.Deserialize<WorkerResult>(contents.GetBuffer().AsSpan(0, (int)contents.Length), JsonOptions);
            if (result is null || result.Version != 1 || result.Id != id || result.Percent is < 0 or > 100 ||
                result.State is not ("running" or "completed" or "failed" or "cancelled")) throw InvalidResult();
            return result;
        }
        catch (FileNotFoundException) { return null; }
        catch (JsonException) { throw InvalidResult(); }
    }

    private async Task MarkCancelledAsync(string id)
    {
        EnsureDirectories();
        var path = Path.Combine(Requests, id + ".cancel");
        if (!File.Exists(path))
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await stream.FlushAsync();
            stream.Flush(true);
        }
    }

    private void Clean(string id)
    {
        EnsureDirectories();
        // Remove the request first: a worker must never observe a request with its terminal result removed.
        File.Delete(Path.Combine(Requests, id + ".json"));
        File.Delete(Path.Combine(Results, id + ".json"));
        File.Delete(Path.Combine(Requests, id + ".cancel"));
    }

    private void EnsureDirectories()
    {
        foreach (var path in new[] { _root, Requests, Results })
        {
            for (var entry = new DirectoryInfo(path); entry is not null; entry = entry.Parent)
                if (entry.LinkTarget is not null || (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                    throw new SubtitleGenerationException("subtitle_worker_unavailable", "The subtitle worker exchange directory is unsafe.");
            Directory.CreateDirectory(path);
        }
    }

    private static SubtitleGenerationException InvalidResult() =>
        new("subtitle_worker_invalid", "The local worker returned an invalid response. Existing subtitles were not changed.");

    private static string SafeStage(string? stage) => stage?.ToLowerInvariant() switch
    {
        "probing" => "Inspecting audio",
        "extracting" => "Extracting audio",
        "loading" or "loading_models" => "Loading local models",
        "transcribing" => "Transcribing audio",
        "translating" => "Translating to Romanian",
        "formatting" => "Formatting subtitles",
        _ => "Processing audio",
    };

    private static SubtitleGenerationException WorkerError(string? code) => code switch
    {
        "no_audio" => new("subtitle_no_audio", "The video has no audio stream."),
        "models_missing" => new("subtitle_models_missing", "Download the local subtitle models before generating subtitles."),
        "path_denied" => new("subtitle_worker_path_denied", "The worker cannot access this video. Check its read-only media mounts and allowed roots."),
        "insufficient_space" => new("subtitle_worker_space", "The worker has insufficient temporary storage for this video."),
        "timeout" or "media_too_long" => new("subtitle_worker_limit", "The video exceeded the worker's configured time or duration limit."),
        "translation_failed" => new("subtitle_translation_failed", "Romanian translation failed. Existing subtitles were not changed."),
        _ => new("subtitle_transcription_failed", "The local worker could not generate subtitles for this video. Existing subtitles were not changed."),
    };

    private sealed record WorkerRequest(int Version, string Id, string VideoPath, bool Romanian);
    private sealed record WorkerResult(int Version, string Id, string State, string? Stage, int Percent,
        string? EnglishSrt, string? RomanianSrt, string? ErrorCode);
}
