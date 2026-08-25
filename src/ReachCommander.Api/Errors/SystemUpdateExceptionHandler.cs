using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Api.Errors;

public sealed class SystemUpdateExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<SystemUpdateExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not SystemUpdateException updateException)
        {
            return false;
        }

        var status = StatusFor(updateException.Code);
        logger.LogInformation(
            "System update request failed with {ErrorCode} and {ExceptionType}.",
            updateException.Code,
            exception.GetType().Name);
        httpContext.Response.StatusCode = status;
        if (status == StatusCodes.Status429TooManyRequests)
        {
            httpContext.Response.Headers.RetryAfter = "30";
        }

        var details = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(updateException.Code),
            Detail = updateException.PublicDetail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
        };
        details.Extensions["code"] = updateException.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static int StatusFor(string code) => code switch
    {
        "system_update_check_rate_limited" => StatusCodes.Status429TooManyRequests,
        "system_update_blocked_by_operations" or "system_update_in_progress" =>
            StatusCodes.Status409Conflict,
        "system_update_failed" => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    private static string TitleFor(string code) => code switch
    {
        "system_update_check_rate_limited" => "Update check rate limited",
        "system_update_blocked_by_operations" => "Update blocked by active operations",
        "system_update_in_progress" => "System update in progress",
        "system_update_protocol_incompatible" => "Host updater incompatible",
        "system_update_failed" => "System update failed",
        _ => "System updates unavailable",
    };
}
