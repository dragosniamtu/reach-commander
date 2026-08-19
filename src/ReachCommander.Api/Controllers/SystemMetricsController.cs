using Microsoft.AspNetCore.Mvc;
using ReachCommander.Api.Contracts.SystemMetrics;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/system-metrics")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SystemMetricsController(
    IHardwareMetricsSnapshotProvider snapshotProvider) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<SystemMetricsDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<SystemMetricsDto> Get() =>
        Ok(SystemMetricsDto.FromSnapshot(snapshotProvider.GetCurrent()));
}
