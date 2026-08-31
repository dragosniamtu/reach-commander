using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ReachCommander.Api.Authentication;
using ReachCommander.Api.Contracts.SourceManagement;
using ReachCommander.Application.SourceManagement;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/source-management")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class SourceManagementController(ISourceManagementService service)
    : ControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<SourceManagementCapabilityDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SourceManagementCapabilityDto>> GetStatus(
        CancellationToken cancellationToken) =>
        Ok(SourceManagementCapabilityDto.FromModel(
            await service.GetStatusAsync(cancellationToken)));

    [HttpPost("sources")]
    [EnableRateLimiting(AuthenticationConfiguration.SourceManagementPolicy)]
    [ProducesResponseType<SourceManagementOperationDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<SourceManagementOperationDto>> Add(
        SourceAddRequestDto request,
        CancellationToken cancellationToken)
    {
        var operation = SourceManagementOperationDto.FromModel(
            await service.AddAsync(request.ToModel(), cancellationToken));
        return AcceptedAtAction(
            nameof(GetOperation),
            new { operationId = operation.OperationId },
            operation);
    }

    [HttpGet("operations/{operationId:guid}")]
    [ProducesResponseType<SourceManagementOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<SourceManagementOperationDto>> GetOperation(
        Guid operationId,
        CancellationToken cancellationToken) =>
        Ok(SourceManagementOperationDto.FromModel(
            await service.GetOperationAsync(operationId, cancellationToken)));
}
