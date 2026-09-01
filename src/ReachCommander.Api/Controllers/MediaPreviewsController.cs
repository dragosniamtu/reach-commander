using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ReachCommander.Api.Authentication;
using ReachCommander.Api.Contracts.MediaPreviews;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/media-previews")]
public sealed class MediaPreviewsController(IMediaPreviewService service) : ControllerBase
{
    [HttpPost]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    [ProducesResponseType<MediaPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<MediaPreviewDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<MediaPreviewDto>> Create(
        CreateMediaPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        var preview = MediaPreviewDto.FromModel(
            await service.CreateAsync(request.ToCommand(), cancellationToken));
        return preview.Phase == MediaPreviewPhase.Ready
            ? Ok(preview)
            : AcceptedAtAction(nameof(Get), new { sessionId = preview.SessionId }, preview);
    }

    [HttpGet("{sessionId:guid}")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewAssetPolicy)]
    [ProducesResponseType<MediaPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MediaPreviewDto>> Get(
        Guid sessionId,
        CancellationToken cancellationToken) => Ok(MediaPreviewDto.FromModel(
        await service.GetAsync(sessionId, cancellationToken)));

    [HttpGet("{sessionId:guid}/content")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewAssetPolicy)]
    public async Task<IActionResult> Content(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var asset = await service.OpenDirectContentAsync(sessionId, cancellationToken);
        return File(
            asset.Content,
            asset.ContentType,
            enableRangeProcessing: asset.EnableRanges);
    }

    [HttpGet("{sessionId:guid}/hls/{assetName}")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewAssetPolicy)]
    public async Task<IActionResult> Hls(
        Guid sessionId,
        string assetName,
        CancellationToken cancellationToken)
    {
        var asset = await service.OpenHlsAssetAsync(
            sessionId,
            assetName,
            cancellationToken);
        return File(asset.Content, asset.ContentType, enableRangeProcessing: false);
    }

    [HttpPut("{sessionId:guid}/subtitle")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    [ProducesResponseType<MediaPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<MediaPreviewDto>> SelectSubtitle(
        Guid sessionId,
        SelectMediaPreviewSubtitleRequestDto request,
        CancellationToken cancellationToken) => Ok(MediaPreviewDto.FromModel(
        await service.SelectSubtitleAsync(
            sessionId,
            request.SubtitlePath,
            cancellationToken)));

    [HttpPost("{sessionId:guid}/fallback")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    [ProducesResponseType<MediaPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<MediaPreviewDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<MediaPreviewDto>> Fallback(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var preview = MediaPreviewDto.FromModel(
            await service.RequestFallbackAsync(sessionId, cancellationToken));
        return preview.Phase == MediaPreviewPhase.Ready
            ? Ok(preview)
            : AcceptedAtAction(nameof(Get), new { sessionId }, preview);
    }

    [HttpPost("{sessionId:guid}/subtitle-save-plans")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    [ProducesResponseType<SubtitleSavePlanDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SubtitleSavePlanDto>> PlanSubtitleSave(
        Guid sessionId,
        CreateSubtitleSavePlanRequestDto request,
        CancellationToken cancellationToken) => Ok(SubtitleSavePlanDto.FromModel(
        await service.PlanSubtitleSaveAsync(
            sessionId,
            request.OffsetMilliseconds,
            cancellationToken)));

    [HttpPost("subtitle-save-plans/{planId:guid}/execute")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    [ProducesResponseType<SubtitleSaveResultDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SubtitleSaveResultDto>> ExecuteSubtitleSave(
        Guid planId,
        CancellationToken cancellationToken) => Ok(SubtitleSaveResultDto.FromModel(
        await service.ExecuteSubtitleSaveAsync(planId, cancellationToken)));

    [HttpDelete("{sessionId:guid}")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Close(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await service.CloseAsync(sessionId, cancellationToken);
        return NoContent();
    }
}
