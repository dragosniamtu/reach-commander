using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.Authentication;
using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed record SubtitleVideoSnapshot(string Path, long Length, long LastWriteTimeUtcTicks);

internal sealed record StoredSubtitleJob(SubtitleGenerationRequest Request, SubtitleJobStatus Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SubtitleVideoSnapshot>? Snapshots = null);

internal sealed class SubtitleJobStore(AuthenticationDataPaths paths, IOptions<SubtitleGenerationOptions> options)
{
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, StoredSubtitleJob> _jobs = [];
    private readonly string _directory = Path.Combine(paths.RootPath, "subtitle-generation", "jobs");
    private bool _loaded;

    internal static bool IsActive(SubtitleJobPhase phase) => phase is SubtitleJobPhase.Queued or
        SubtitleJobPhase.Discovering or SubtitleJobPhase.Running or SubtitleJobPhase.Cancelling;

    internal async Task<SubtitleJobStatus> EnqueueAsync(SubtitleGenerationRequest request, CancellationToken token,
        IReadOnlyList<SubtitleVideoSnapshot>? snapshots = null)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (_jobs.Values.Count(job => IsActive(job.Status.Phase)) >= options.Value.MaximumQueuedJobs)
                throw new SubtitleGenerationException("subtitle_capacity_reached", "The subtitle queue is full. Wait for an active job to finish.");
            var now = DateTimeOffset.UtcNow;
            var files = request.Paths?.Select(path => new SubtitleFileStatus(path, SubtitleFilePhase.Pending)).ToArray() ?? [];
            var status = new SubtitleJobStatus(Guid.NewGuid(), request.SourceId, request.Path, request.English, request.Romanian,
                SubtitleJobPhase.Queued, "Queued", null, 0, files, now, now);
            await SaveAsync(new(request, status, snapshots), token);
            await TrimAsync();
            return status;
        }
        finally { _gate.Release(); }
    }

    internal async Task<IReadOnlyList<SubtitleJobStatus>> ListAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            return _jobs.Values.Select(job => job.Status).OrderByDescending(job => job.CreatedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    internal async Task<SubtitleJobStatus> GetAsync(Guid id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { await LoadAsync(token); return Required(id).Status; }
        finally { _gate.Release(); }
    }

    internal async Task<SubtitleJobStatus> UpdateAsync(Guid id, Func<SubtitleJobStatus, SubtitleJobStatus> update, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            var current = Required(id);
            var status = update(current.Status) with { UpdatedAt = DateTimeOffset.UtcNow };
            await SaveAsync(current with { Status = status }, token);
            return status;
        }
        finally { _gate.Release(); }
    }

    internal async Task<StoredSubtitleJob?> TakeNextAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            if (_jobs.Values.Any(job => job.Status.Phase is SubtitleJobPhase.Discovering or SubtitleJobPhase.Running or SubtitleJobPhase.Cancelling))
                return null;
            var job = _jobs.Values.Where(job => job.Status.Phase == SubtitleJobPhase.Queued)
                .OrderBy(job => job.Status.CreatedAt).FirstOrDefault();
            if (job is null) return null;
            job = job with { Status = job.Status with
            {
                Phase = SubtitleJobPhase.Discovering,
                Stage = job.Request.Paths is null ? "Finding videos" : "Preparing",
                UpdatedAt = DateTimeOffset.UtcNow,
            } };
            await SaveAsync(job, token);
            return job;
        }
        finally { _gate.Release(); }
    }

    internal async Task RecoverAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            await LoadAsync(token);
            foreach (var job in _jobs.Values.ToArray())
            {
                if (!IsActive(job.Status.Phase) || job.Status.Phase == SubtitleJobPhase.Queued) continue;
                await SaveAsync(job with { Status = job.Status with
                {
                    Phase = SubtitleJobPhase.Interrupted, Stage = "Interrupted by service restart",
                    Error = "The service restarted. Run this selection again to resume; completed videos can be skipped.",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Files = job.Status.Files.Select(file => file.Phase is SubtitleFilePhase.Pending or SubtitleFilePhase.Running
                        ? file with { Phase = SubtitleFilePhase.Cancelled, Error = "Interrupted by service restart." } : file).ToArray(),
                } }, token);
            }
        }
        finally { _gate.Release(); }
    }

    private StoredSubtitleJob Required(Guid id) => _jobs.TryGetValue(id, out var job) ? job :
        throw new SubtitleGenerationException("subtitle_job_not_found", "This subtitle job could not be found.");

    private async Task SaveAsync(StoredSubtitleJob job, CancellationToken token)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(job, JsonOptions).Length > MaximumDocumentBytes)
            throw new SubtitleGenerationException("subtitle_selection_too_large", "This selection exceeds the subtitle history size limit. Select a smaller folder.");
        await AtomicJsonFile.WriteAsync(Path.Combine(_directory, $"{job.Status.JobId:N}.json"), job, token);
        _jobs[job.Status.JobId] = job;
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (_loaded) return;
        if (!Directory.Exists(_directory))
        {
            _loaded = true;
            return;
        }
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var file = new FileInfo(path);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ||
                file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaximumDocumentBytes)
                throw new SubtitleGenerationException("subtitle_state_unavailable", "The subtitle job history needs administrator attention.");
            var job = await AtomicJsonFile.ReadAsync<StoredSubtitleJob>(path, token);
            if (job.Status.JobId != id) throw new SubtitleGenerationException("subtitle_state_unavailable", "The subtitle job history is invalid.");
            _jobs[id] = job;
        }
        _loaded = true;
    }

    private Task TrimAsync()
    {
        foreach (var old in _jobs.Values.Where(job => !IsActive(job.Status.Phase))
            .OrderByDescending(job => job.Status.CreatedAt).Skip(100).ToArray())
        {
            File.Delete(Path.Combine(_directory, $"{old.Status.JobId:N}.json"));
            _jobs.Remove(old.Status.JobId);
        }
        return Task.CompletedTask;
    }
}
