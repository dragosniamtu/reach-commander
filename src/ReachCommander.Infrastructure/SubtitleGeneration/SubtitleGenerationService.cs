using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReachCommander.Application.SubtitleGeneration;
using ReachCommander.Infrastructure.FileOperations.Persistence;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal sealed class SubtitleGenerationService(
    SubtitleDiscovery discovery, SubtitleJobStore store, ISubtitleEngine engine, ISubtitleInstaller installer,
    IOptions<SubtitleGenerationOptions> options, ILogger<SubtitleGenerationService> logger, SubtitleWorkerReadiness readiness)
    : BackgroundService, ISubtitleGenerationService
{
    private readonly FileOperationQueue _queue = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private bool _startupFailed;

    public Task<SubtitleGenerationCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) =>
        options.Value.Enabled && _startupFailed
            ? Task.FromResult(new SubtitleGenerationCapabilities(true, false, false, false, "subtitle_state_unavailable",
                "Subtitle generation needs administrator attention before it can start."))
            : readiness.GetCapabilitiesAsync(cancellationToken);

    public async Task<SubtitleJobStatus> SubmitAsync(SubtitleGenerationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!options.Value.Enabled)
            throw new SubtitleGenerationException("subtitle_generation_disabled", "Subtitle generation is not enabled. Configure the local worker first.");
        if (_startupFailed)
            throw new SubtitleGenerationException("subtitle_state_unavailable", "Subtitle generation needs administrator attention before it can start.");
        if (!request.English && !request.Romanian)
            throw new SubtitleGenerationException("subtitle_languages_required", "Select at least one subtitle language.");
        var resolved = await discovery.ValidateSelectionAsync(request.SourceId, request.Path, cancellationToken);
        await readiness.RequireAvailableAsync(request.Romanian, cancellationToken);
        var status = await store.EnqueueAsync(request with { SourceId = resolved.Source.Id, Path = resolved.LogicalPath }, cancellationToken);
        _queue.Signal();
        return status;
    }

    public Task<IReadOnlyList<SubtitleJobStatus>> ListAsync(CancellationToken cancellationToken) => store.ListAsync(cancellationToken);
    public Task<SubtitleJobStatus> GetAsync(Guid jobId, CancellationToken cancellationToken) => store.GetAsync(jobId, cancellationToken);

    public async Task<SubtitleJobStatus> CancelAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var status = await store.UpdateAsync(jobId, current => current.Phase switch
        {
            SubtitleJobPhase.Queued => current with { Phase = SubtitleJobPhase.Cancelled, Stage = "Cancelled" },
            SubtitleJobPhase.Running or SubtitleJobPhase.Discovering => current with { Phase = SubtitleJobPhase.Cancelling, Stage = "Cancelling" },
            _ => current,
        }, cancellationToken);
        if (_running.TryGetValue(jobId, out var source))
        {
            try { await source.CancelAsync(); }
            catch (ObjectDisposedException) { /* The job reached its terminal state concurrently. */ }
        }
        _queue.Signal();
        return status;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;
        try
        {
            await engine.CancelAbandonedAsync(stoppingToken);
            await installer.RecoverAsync(stoppingToken);
            await store.RecoverAsync(stoppingToken);
            _queue.Signal();
            while (!stoppingToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                idle.CancelAfter(TimeSpan.FromSeconds(10));
                var idleMaintenance = false;
                try { await _queue.WaitAsync(idle.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { idleMaintenance = true; }
                // No inference request is active here. Reap late acknowledgements even when no further jobs arrive.
                while (await ProcessNextAsync(stoppingToken)) await MaintainWorkerAsync(stoppingToken);
                if (idleMaintenance) await MaintainWorkerAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _startupFailed = true;
            logger.LogError("Subtitle job service stopped with {ExceptionType}. Check its private state directory.", exception.GetType().Name);
        }
    }

    private async Task MaintainWorkerAsync(CancellationToken cancellationToken)
    {
        try { await engine.CancelAbandonedAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SubtitleGenerationException)
        {
            // Requests retain their cancellation markers; a later idle cycle retries cleanup.
            logger.LogWarning("Subtitle mailbox maintenance will retry after {ExceptionType}.", exception.GetType().Name);
        }
    }

    internal async Task<bool> ProcessNextAsync(CancellationToken stoppingToken)
    {
        var job = await store.TakeNextAsync(stoppingToken);
        if (job is null) return false;
        var id = job.Status.JobId;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[id] = cancellation;
        var token = cancellation.Token;
        try
        {
            if ((await store.GetAsync(id, token)).Phase == SubtitleJobPhase.Cancelling) await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            var files = await discovery.DiscoverAsync(job.Request, token);
            await store.UpdateAsync(id, current => current with
            {
                Files = files,
                Phase = current.Phase == SubtitleJobPhase.Cancelling ? current.Phase : SubtitleJobPhase.Running,
                Stage = "Preparing",
            }, token);
            foreach (var file in files.Where(file => file.Phase == SubtitleFilePhase.Pending))
            {
                token.ThrowIfCancellationRequested();
                await UpdateFileAsync(id, file.Path, SubtitleFilePhase.Running, null, token);
                try
                {
                    var video = await discovery.ResolveVideoAsync(job.Request.SourceId, file.Path, token);
                    if (job.Request.SkipExisting && await installer.IsAlreadyGeneratedAsync(video, job.Request.English, job.Request.Romanian, token))
                    {
                        await UpdateFileAsync(id, file.Path, SubtitleFilePhase.Skipped, null, token);
                        continue;
                    }
                    var result = await engine.GenerateAsync(video, job.Request.Romanian,
                        progress => UpdateProgressAsync(id, progress, token), token);
                    token.ThrowIfCancellationRequested();
                    await UpdateProgressAsync(id, new("Saving subtitles", 99), token);
                    await installer.InstallAsync(video, job.Request.English, job.Request.Romanian, result, token);
                    // Installation is non-cancellable once originals move; record committed output even if cancellation arrived meanwhile.
                    await UpdateFileAsync(id, file.Path, SubtitleFilePhase.Completed, null, CancellationToken.None);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning("Subtitle job {JobId} file failed with {ExceptionType}", id, exception.GetType().Name);
                    await UpdateFileAsync(id, file.Path, SubtitleFilePhase.Failed, SafeError(exception), CancellationToken.None);
                }
            }
            token.ThrowIfCancellationRequested();
            await store.UpdateAsync(id, current => current with
            {
                Phase = SubtitleJobPhase.Completed, CurrentPercent = 100,
                Stage = current.Files.Count == 0 ? "No supported videos found" :
                    current.Files.Any(file => file.Phase == SubtitleFilePhase.Failed) ? "Completed with errors" : "Completed",
            }, CancellationToken.None);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await store.UpdateAsync(id, current => current with
            {
                Phase = stoppingToken.IsCancellationRequested ? SubtitleJobPhase.Interrupted : SubtitleJobPhase.Cancelled,
                Stage = stoppingToken.IsCancellationRequested ? "Interrupted by service restart" : "Cancelled",
                Files = current.Files.Select(file => file.Phase is SubtitleFilePhase.Pending or SubtitleFilePhase.Running
                    ? file with { Phase = SubtitleFilePhase.Cancelled } : file).ToArray(),
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Subtitle job {JobId} failed with {ExceptionType}", id, exception.GetType().Name);
            await store.UpdateAsync(id, current => current with
            {
                Phase = SubtitleJobPhase.Failed, Stage = "Failed", Error = SafeError(exception),
                Files = current.Files.Select(file => file.Phase is SubtitleFilePhase.Pending or SubtitleFilePhase.Running
                    ? file with { Phase = SubtitleFilePhase.Failed, Error = "Job could not continue." } : file).ToArray(),
            }, CancellationToken.None);
        }
        finally { _running.TryRemove(id, out _); }
        return true;
    }

    private async Task UpdateProgressAsync(Guid id, SubtitleEngineProgress progress, CancellationToken token) =>
        await store.UpdateAsync(id, current => current with
        {
            Stage = current.Phase == SubtitleJobPhase.Cancelling ? "Cancelling" : progress.Stage,
            CurrentPercent = Math.Clamp(progress.Percent, 0, 100),
        }, token);

    private Task<SubtitleJobStatus> UpdateFileAsync(Guid id, string path, SubtitleFilePhase phase, string? error, CancellationToken token) =>
        store.UpdateAsync(id, current => current with
        {
            CurrentPath = path, CurrentPercent = phase == SubtitleFilePhase.Running ? 0 : 100,
            Files = current.Files.Select(file => file.Path == path ? file with { Phase = phase, Error = error } : file).ToArray(),
        }, token);

    private static string SafeError(Exception exception) => exception is SubtitleGenerationException error
        ? error.PublicDetail : "This video could not be processed safely. Its existing subtitles were preserved; check server logs.";
}
