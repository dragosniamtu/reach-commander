using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ReachCommander.Api.Authentication;
using ReachCommander.Application.SubtitleGeneration;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/subtitle-jobs")]
public sealed class SubtitleJobsController(ISubtitleGenerationService service) : ControllerBase
{
    private const long MaximumRequestBytes = 1024 * 1024;

    [HttpGet("capabilities")]
    public async Task<ActionResult<SubtitleGenerationCapabilities>> Capabilities(CancellationToken cancellationToken) =>
        Ok(await service.GetCapabilitiesAsync(cancellationToken));

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SubtitleJobStatus>>> List(CancellationToken cancellationToken) =>
        Ok(await service.ListAsync(cancellationToken));

    [HttpPost]
    [RequestSizeLimit(MaximumRequestBytes)]
    [RejectOversizedContentLength(MaximumRequestBytes)]
    [EnableRateLimiting(AuthenticationConfiguration.SubtitleGenerationPolicy)]
    public async Task<ActionResult<SubtitleJobStatus>> Submit(SubtitleJobRequest request, CancellationToken cancellationToken)
    {
        var job = await service.SubmitAsync(new(request.SourceId, request.Path, request.English, request.Romanian,
            request.SkipExisting, request.Paths), cancellationToken);
        return AcceptedAtAction(nameof(Get), new { jobId = job.JobId }, job);
    }

    [HttpGet("{jobId:guid}")]
    public async Task<ActionResult<SubtitleJobStatus>> Get(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await service.GetAsync(jobId, cancellationToken));

    [HttpPost("{jobId:guid}/cancel")]
    public async Task<ActionResult<SubtitleJobStatus>> Cancel(Guid jobId, CancellationToken cancellationToken) =>
        Ok(await service.CancelAsync(jobId, cancellationToken));
}

public sealed record SubtitleJobRequest(
    [Required, StringLength(128)] string SourceId,
    [Required, StringLength(SubtitleGenerationRequest.MaximumPathLength)] string Path,
    bool English = true, bool Romanian = false, bool SkipExisting = true, IReadOnlyList<string>? Paths = null);
