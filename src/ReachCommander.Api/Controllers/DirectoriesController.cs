using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts;
using ReachCommander.Api.Contracts.Directories;
using ReachCommander.Application.Directories;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/directories")]
public sealed class DirectoriesController(IDirectoryMutationService service) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType<FileEntryDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<FileEntryDto>> Create(
        CreateDirectoryRequestDto request,
        CancellationToken cancellationToken)
    {
        var entry = FileEntryDto.FromEntry(
            await service.CreateAsync(request.ToModel(), cancellationToken));
        return Created($"/api/files?sourceId={Uri.EscapeDataString(request.SourceId)}&path={Uri.EscapeDataString(entry.RelativePath)}", entry);
    }
}
