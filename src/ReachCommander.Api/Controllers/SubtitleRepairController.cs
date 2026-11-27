using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ReachCommander.Api.Authentication;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/media-previews")]
public sealed class SubtitleRepairController(ISubtitleRepairService service) : ControllerBase
{
    [HttpGet("repair-capabilities")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewAssetPolicy)]
    public Task<SubtitleRepairCapabilities> Capabilities(CancellationToken token) => service.GetCapabilitiesAsync(token);

    [HttpPost("{sessionId:guid}/repair")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    public Task<SubtitleRepairStatus> Start(Guid sessionId, CancellationToken token) => service.StartAsync(sessionId, token);

    [HttpGet("{sessionId:guid}/repair")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewAssetPolicy)]
    public ValueTask<SubtitleRepairStatus> Status(Guid sessionId, CancellationToken token) => service.GetAsync(sessionId, token);

    [HttpDelete("{sessionId:guid}/repair")]
    [EnableRateLimiting(AuthenticationConfiguration.MediaPreviewPolicy)]
    public Task<SubtitleRepairStatus> Cancel(Guid sessionId, CancellationToken token) => service.CancelAsync(sessionId, token);
}
