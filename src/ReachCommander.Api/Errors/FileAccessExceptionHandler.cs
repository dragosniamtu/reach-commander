using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.SystemMetrics;

namespace ReachCommander.Api.Errors;

public sealed class FileAccessExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<FileAccessExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var descriptor = Describe(exception);
        if (descriptor.Status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unexpected failure while processing {RequestPath}", httpContext.Request.Path);
        }
        else
        {
            logger.LogInformation(
                "Request failed with {ErrorCode} at {RequestPath}",
                descriptor.Code,
                httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = descriptor.Status;
        var details = new ProblemDetails
        {
            Status = descriptor.Status,
            Title = descriptor.Title,
            Detail = descriptor.Detail,
            Type = $"https://httpstatuses.io/{descriptor.Status}",
            Instance = httpContext.Request.Path,
        };
        details.Extensions["code"] = descriptor.Code;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static ErrorDescriptor Describe(Exception exception) => exception switch
    {
        InvalidLogicalPathException error => new(
            StatusCodes.Status400BadRequest,
            "Invalid logical path",
            "invalid_path",
            error.Message),
        PathConfinementException error => new(
            StatusCodes.Status403Forbidden,
            "Path is outside its source",
            "path_forbidden",
            error.Message),
        SourceNotFoundException error => new(
            StatusCodes.Status404NotFound,
            "Source not found",
            "source_not_found",
            error.Message),
        EntryNotFoundException error => new(
            StatusCodes.Status404NotFound,
            "Entry not found",
            "entry_not_found",
            error.Message),
        SourceUnavailableException error => new(
            StatusCodes.Status503ServiceUnavailable,
            "Source unavailable",
            "source_unavailable",
            error.Message),
        HardwareMetricsNotReadyException => new(
            StatusCodes.Status503ServiceUnavailable,
            "Hardware metrics not ready",
            "metrics_not_ready",
            "Hardware metrics have not completed their first sample."),
        _ => new(
            StatusCodes.Status500InternalServerError,
            "Unexpected server error",
            "unexpected_error",
            "The request could not be completed."),
    };

    private sealed record ErrorDescriptor(int Status, string Title, string Code, string Detail);
}
