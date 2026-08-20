using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.BatchRenames;
using ReachCommander.Application.BatchRenames;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/batch-renames")]
public sealed class BatchRenamesController(IBatchRenameService service) : ControllerBase
{
    [HttpPost("preview")]
    [ProducesResponseType<BatchRenamePreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BatchRenamePreviewDto>> Preview(
        BatchRenamePreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(BatchRenamePreviewDto.FromModel(
            await service.PreviewAsync(request.ToCommand(), cancellationToken)));

    [HttpPost("{planId:guid}/execute")]
    [ProducesResponseType<BatchRenameOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BatchRenameOperationDto>> Execute(
        Guid planId,
        CancellationToken cancellationToken) =>
        Ok(BatchRenameOperationDto.FromModel(
            await service.ExecuteAsync(planId, cancellationToken)));

    [HttpPost("{operationId:guid}/undo")]
    [ProducesResponseType<BatchRenameOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<BatchRenameOperationDto>> Undo(
        Guid operationId,
        CancellationToken cancellationToken) =>
        Ok(BatchRenameOperationDto.FromModel(
            await service.UndoAsync(operationId, cancellationToken)));
}
