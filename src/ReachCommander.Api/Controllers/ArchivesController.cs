using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using ReachCommander.Api.Contracts.Archives;
using ReachCommander.Application.Archives;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/archives")]
public sealed class ArchivesController(IArchiveBrowser archiveBrowser) : ControllerBase
{
    [HttpGet("entries")]
    [ProducesResponseType<ArchiveDirectoryDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ArchiveDirectoryDto>> List(
        [FromQuery, BindRequired] string sourceId,
        [FromQuery, BindRequired] string archivePath,
        [FromQuery, BindRequired] string path,
        CancellationToken cancellationToken)
    {
        var listing = await archiveBrowser.ListAsync(
            new ArchiveLocation(sourceId, archivePath, path),
            cancellationToken);
        return Ok(ArchiveDirectoryDto.FromListing(listing));
    }
}
