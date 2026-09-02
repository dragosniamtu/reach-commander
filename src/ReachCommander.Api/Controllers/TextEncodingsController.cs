using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ReachCommander.Api.Authentication;
using ReachCommander.Api.Contracts.TextEncodings;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/text-encodings")]
public sealed class TextEncodingsController(ITextEncodingService service) : ControllerBase
{
    [HttpPost("preview")]
    [EnableRateLimiting(AuthenticationConfiguration.TextEncodingPolicy)]
    [ProducesResponseType<TextEncodingPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TextEncodingPreviewDto>> Preview(
        TextEncodingPreviewRequestDto request,
        CancellationToken cancellationToken) => Ok(TextEncodingPreviewDto.FromModel(
        await service.PreviewAsync(request.ToModel(), cancellationToken)));

    [HttpPost("{planId:guid}/execute")]
    [EnableRateLimiting(AuthenticationConfiguration.TextEncodingPolicy)]
    [ProducesResponseType<TextEncodingOperationDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<TextEncodingOperationDto>> Execute(
        Guid planId,
        CancellationToken cancellationToken)
    {
        var operation = TextEncodingOperationDto.FromModel(
            await service.ExecuteAsync(planId, cancellationToken));
        return AcceptedAtAction(
            nameof(GetOperation),
            new { operationId = operation.OperationId },
            operation);
    }

    [HttpGet("operations/{operationId:guid}")]
    [EnableRateLimiting(AuthenticationConfiguration.TextEncodingPolicy)]
    [ProducesResponseType<TextEncodingOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TextEncodingOperationDto>> GetOperation(
        Guid operationId,
        CancellationToken cancellationToken) => Ok(TextEncodingOperationDto.FromModel(
        await service.GetAsync(operationId, cancellationToken)));

    [HttpPost("operations/{operationId:guid}/cancel")]
    [EnableRateLimiting(AuthenticationConfiguration.TextEncodingPolicy)]
    [ProducesResponseType<TextEncodingOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<TextEncodingOperationDto>> Cancel(
        Guid operationId,
        CancellationToken cancellationToken) => Ok(TextEncodingOperationDto.FromModel(
        await service.CancelAsync(operationId, cancellationToken)));
}
