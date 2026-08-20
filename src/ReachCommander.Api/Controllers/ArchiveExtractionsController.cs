using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using ReachCommander.Api.Contracts.Archives;
using ReachCommander.Application.Archives;
using ReachCommander.Infrastructure.Archives;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/archive-extractions")]
public sealed class ArchiveExtractionsController(
    IArchiveExtractionService service,
    IOptions<ArchiveOptions> options) : ControllerBase
{
    private const long MaximumRequestBytes = 8L * 1024 * 1024;

    [HttpPost("preview")]
    [RequestSizeLimit(MaximumRequestBytes)]
    [RejectOversizedContentLength(MaximumRequestBytes)]
    [ProducesResponseType<ArchiveExtractionPreviewDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ArchiveExtractionPreviewDto>> Preview(
        ArchiveExtractionPreviewRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.EntryPaths is { Count: var count } && count > options.Value.MaxEntries)
        {
            ModelState.AddModelError(
                nameof(request.EntryPaths),
                $"At most {options.Value.MaxEntries} entry paths may be selected.");
            return ValidationProblem(ModelState);
        }

        var preview = await service.PreviewAsync(request.ToModel(), cancellationToken);
        return Ok(ArchiveExtractionPreviewDto.FromModel(preview));
    }

    [HttpPost("{planId}/execute")]
    [ProducesResponseType<ArchiveExtractionOperationDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<ArchiveExtractionOperationDto>> Execute(
        string planId,
        CancellationToken cancellationToken)
    {
        var operation = ArchiveExtractionOperationDto.FromModel(
            await service.ExecuteAsync(planId, cancellationToken));
        return AcceptedAtAction(
            nameof(GetOperation),
            new { operationId = operation.OperationId },
            operation);
    }

    [HttpGet("{operationId}")]
    [ProducesResponseType<ArchiveExtractionOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ArchiveExtractionOperationDto>> GetOperation(
        string operationId,
        CancellationToken cancellationToken) =>
        Ok(ArchiveExtractionOperationDto.FromModel(
            await service.GetAsync(operationId, cancellationToken)));

    [HttpPost("{operationId}/cancel")]
    [ProducesResponseType<ArchiveExtractionOperationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ArchiveExtractionOperationDto>> Cancel(
        string operationId,
        CancellationToken cancellationToken) =>
        Ok(ArchiveExtractionOperationDto.FromModel(
            await service.CancelAsync(operationId, cancellationToken)));
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class RejectOversizedContentLengthAttribute(long maximumBytes)
    : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var contentLength = context.HttpContext.Request.ContentLength;
        if (contentLength is null || contentLength.Value <= maximumBytes)
        {
            return;
        }

        var details = new ProblemDetails
        {
            Status = StatusCodes.Status413PayloadTooLarge,
            Title = "Request body too large",
            Detail = "The request body exceeds the allowed size.",
            Type = "https://httpstatuses.io/413",
            Instance = context.HttpContext.Request.Path,
        };
        details.Extensions["code"] = "request_too_large";
        context.Result = new ObjectResult(details)
        {
            StatusCode = StatusCodes.Status413PayloadTooLarge,
        };
    }

    public void OnResourceExecuted(ResourceExecutedContext context)
    {
    }
}
