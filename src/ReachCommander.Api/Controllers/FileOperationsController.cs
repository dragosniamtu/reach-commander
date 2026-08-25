using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.FileOperations;
using ReachCommander.Application.FileOperations;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/file-operations")]
public sealed class FileOperationsController(IFileOperationService service) : ControllerBase
{
    [HttpPost("preview")]
    [ProducesResponseType<FileOperationPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FileOperationPreviewDto>> Preview(
        FileOperationPreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(FileOperationPreviewDto.FromModel(
            await service.PreviewAsync(request.ToModel(), cancellationToken)));

    [HttpPost]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FileOperationStatusDto>> Submit(
        FileOperationSubmissionDto request,
        CancellationToken cancellationToken)
    {
        var status = FileOperationStatusDto.FromModel(
            await service.SubmitAsync(request.ToModel(), cancellationToken));
        return AcceptedAtAction(nameof(Get), new { operationId = status.OperationId }, status);
    }

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FileOperationStatusDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FileOperationStatusDto>>> List(
        CancellationToken cancellationToken) =>
        Ok((await service.ListAsync(cancellationToken))
            .Select(FileOperationStatusDto.FromModel)
            .ToArray());

    [HttpGet("{operationId:guid}")]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FileOperationStatusDto>> Get(
        Guid operationId,
        CancellationToken cancellationToken) =>
        Ok(FileOperationStatusDto.FromModel(
            await service.GetAsync(operationId, cancellationToken)));

    [HttpPost("{operationId:guid}/cancel")]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FileOperationStatusDto>> Cancel(
        Guid operationId,
        CancellationToken cancellationToken) =>
        Ok(FileOperationStatusDto.FromModel(
            await service.CancelAsync(operationId, cancellationToken)));

    [HttpDelete("{operationId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Acknowledge(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await service.AcknowledgeAsync(operationId, cancellationToken);
        return NoContent();
    }
}
