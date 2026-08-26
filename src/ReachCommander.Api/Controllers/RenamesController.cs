using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.BatchRenames;
using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/renames")]
public sealed class RenamesController(IBatchRenameService service) : ControllerBase
{
    [HttpPost("preview")]
    [ProducesResponseType<BatchRenamePreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BatchRenamePreviewDto>> Preview(
        ExactRenamePreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(BatchRenamePreviewDto.FromModel(
            await service.PreviewExactAsync(request.ToCommand(), cancellationToken)));
}
