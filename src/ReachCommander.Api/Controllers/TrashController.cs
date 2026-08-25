using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.FileOperations;
using ReachCommander.Api.Contracts.Trash;
using ReachCommander.Application.Trash;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/trash")]
public sealed class TrashController(ITrashService service) : ControllerBase
{
    [HttpPost("preview-delete")]
    [ProducesResponseType<DeletePreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DeletePreviewDto>> PreviewDelete(
        DeletePreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(DeletePreviewDto.FromModel(
            await service.PreviewDeleteAsync(request.ToModel(), cancellationToken)));

    [HttpPost("delete")]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FileOperationStatusDto>> SubmitDelete(
        DeleteSubmissionDto request,
        CancellationToken cancellationToken) =>
        Accepted(FileOperationStatusDto.FromModel(
            await service.SubmitDeleteAsync(request.ToModel(), cancellationToken)));

    [HttpGet]
    [ProducesResponseType<IReadOnlyList<TrashEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<TrashEntryDto>>> List(
        [FromQuery] string? sourceId,
        CancellationToken cancellationToken) =>
        Ok((await service.ListAsync(sourceId, cancellationToken))
            .Select(TrashEntryDto.FromModel)
            .ToArray());

    [HttpPost("preview-restore")]
    [ProducesResponseType<RestorePreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<RestorePreviewDto>> PreviewRestore(
        RestorePreviewRequestDto request,
        CancellationToken cancellationToken) =>
        Ok(RestorePreviewDto.FromModel(
            await service.PreviewRestoreAsync(request.ToModel(), cancellationToken)));

    [HttpPost("restore")]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FileOperationStatusDto>> SubmitRestore(
        RestoreSubmissionDto request,
        CancellationToken cancellationToken) =>
        Accepted(FileOperationStatusDto.FromModel(
            await service.SubmitRestoreAsync(request.ToModel(), cancellationToken)));

    [HttpDelete("items")]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FileOperationStatusDto>> PermanentlyDelete(
        TrashPermanentDeleteRequestDto request,
        CancellationToken cancellationToken) =>
        Accepted(FileOperationStatusDto.FromModel(
            await service.PermanentlyDeleteAsync(request.ToModel(), cancellationToken)));

    [HttpDelete]
    [ProducesResponseType<FileOperationStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FileOperationStatusDto>> EmptyTrash(
        EmptyTrashRequestDto request,
        CancellationToken cancellationToken) =>
        Accepted(FileOperationStatusDto.FromModel(
            await service.EmptyAsync(request.ToModel(), cancellationToken)));
}
