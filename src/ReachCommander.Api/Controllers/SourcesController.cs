using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts;
using ReachCommander.Application.Sources;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/sources")]
public sealed class SourcesController(ISourceCatalog sourceCatalog) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SourceDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SourceDto>>> GetSources(
        CancellationToken cancellationToken)
    {
        var sources = await sourceCatalog.GetSnapshotsAsync(cancellationToken);
        return Ok(sources.Select(SourceDto.FromSnapshot).ToArray());
    }
}
