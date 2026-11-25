using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.SubtitleGeneration;

namespace ReachCommander.Api.Errors;

public sealed class SubtitleGenerationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not SubtitleGenerationException error) return false;
        var status = error.Code switch
        {
            "subtitle_job_not_found" => StatusCodes.Status404NotFound,
            "source_read_only" => StatusCodes.Status403Forbidden,
            "subtitle_capacity_reached" => StatusCodes.Status429TooManyRequests,
            "subtitle_generation_disabled" or "subtitle_state_unavailable" or "subtitle_worker_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status422UnprocessableEntity,
        };
        context.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status, Title = "Subtitle generation unavailable", Detail = error.PublicDetail,
            Type = $"https://httpstatuses.io/{status}", Instance = context.Request.Path,
        };
        details.Extensions["code"] = error.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context, ProblemDetails = details, Exception = exception,
        });
    }
}
