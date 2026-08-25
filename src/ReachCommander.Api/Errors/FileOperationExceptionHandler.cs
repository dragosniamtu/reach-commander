using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.FileOperations;

namespace ReachCommander.Api.Errors;

public sealed class FileOperationExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<FileOperationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not FileOperationException operationException)
        {
            return false;
        }

        var status = StatusFor(operationException.Code);
        logger.LogInformation(
            "File operation request failed with {ErrorCode} and {ExceptionType}",
            operationException.Code,
            exception.GetType().Name);
        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(operationException.Code),
            Detail = operationException.PublicDetail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
        };
        details.Extensions["code"] = operationException.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static int StatusFor(string code) => code switch
    {
        "operation_plan_not_found" => StatusCodes.Status404NotFound,
        "operation_plan_expired" => StatusCodes.Status410Gone,
        "operation_plan_stale" or
        "destination_conflict" or
        "trash_manifest_invalid" or
        "trash_restore_conflict" or
        "move_source_not_removed" => StatusCodes.Status409Conflict,
        "source_read_only" => StatusCodes.Status403Forbidden,
        "source_unavailable" or "destination_unavailable" =>
            StatusCodes.Status503ServiceUnavailable,
        "insufficient_storage" => StatusCodes.Status507InsufficientStorage,
        "unsafe_symbolic_link" or
        "trash_unavailable" or
        "permanent_delete_confirmation_required" =>
            StatusCodes.Status422UnprocessableEntity,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string TitleFor(string code) => code switch
    {
        "operation_plan_not_found" => "Operation plan not found",
        "operation_plan_expired" => "Operation plan expired",
        "operation_plan_stale" => "Operation plan is stale",
        "destination_conflict" => "Destination conflict",
        "source_read_only" => "Source is read-only",
        "source_unavailable" => "Source unavailable",
        "destination_unavailable" => "Destination unavailable",
        "insufficient_storage" => "Insufficient storage",
        "unsafe_symbolic_link" => "Unsafe symbolic link",
        "trash_unavailable" => "Managed Trash unavailable",
        "trash_manifest_invalid" => "Trash record invalid",
        "trash_restore_conflict" => "Trash restore conflict",
        "permanent_delete_confirmation_required" => "Permanent deletion confirmation required",
        _ => "Invalid file operation",
    };
}
