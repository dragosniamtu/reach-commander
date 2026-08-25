using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.SystemUpdates;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/system-update")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SystemUpdatesController(ISystemUpdateService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SystemUpdateStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemUpdateStatusDto>> Get(CancellationToken token) =>
        Ok(SystemUpdateStatusDto.FromModel(await service.GetAsync(token)));

    [HttpPost("check")]
    [ProducesResponseType<SystemUpdateStatusDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SystemUpdateStatusDto>> Check(CancellationToken token)
    {
        if (HasBody())
        {
            return InvalidBody();
        }

        return Ok(SystemUpdateStatusDto.FromModel(await service.CheckAsync(token)));
    }

    [HttpPost("apply")]
    [ProducesResponseType<SystemUpdateStatusDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<SystemUpdateStatusDto>> Apply(CancellationToken token)
    {
        if (HasBody())
        {
            return InvalidBody();
        }

        return Accepted(SystemUpdateStatusDto.FromModel(await service.ApplyAsync(token)));
    }

    private bool HasBody() =>
        Request.ContentLength is > 0 || Request.Headers.TransferEncoding.Count > 0;

    private BadRequestObjectResult InvalidBody()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid system update request",
            Detail = "System update targets are selected only by the trusted host configuration.",
            Type = "https://httpstatuses.io/400",
            Instance = Request.Path,
        };
        problem.Extensions["code"] = "invalid_system_update_request";
        return BadRequest(problem);
    }
}
