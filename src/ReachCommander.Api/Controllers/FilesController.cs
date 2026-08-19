using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts;
using ReachCommander.Application.Files;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/files")]
public sealed class FilesController(IFileBrowser fileBrowser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<FileEntryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<FileEntryDto>>> List(
        [FromQuery] string sourceId,
        [FromQuery] string path = "/",
        CancellationToken cancellationToken = default)
    {
        var entries = await fileBrowser.ListAsync(sourceId, path, cancellationToken);
        return Ok(entries.Select(FileEntryDto.FromEntry).ToArray());
    }

    [HttpGet("info")]
    [ProducesResponseType<FileEntryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FileEntryDto>> GetInfo(
        [FromQuery] string sourceId,
        [FromQuery] string path,
        CancellationToken cancellationToken)
    {
        var entry = await fileBrowser.GetInfoAsync(sourceId, path, cancellationToken);
        return Ok(FileEntryDto.FromEntry(entry));
    }
}
