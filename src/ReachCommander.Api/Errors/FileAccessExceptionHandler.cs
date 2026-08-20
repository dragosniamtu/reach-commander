using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Application.Uploads;

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
        if (exception is UploadNameConflictException conflict)
        {
            details.Extensions["fileNames"] = conflict.FileNames;
        }
        else if (exception is UploadCleanupRequiredException cleanup)
        {
            details.Extensions["fileNames"] = cleanup.FileNames;
        }

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
        UploadEmptyException error => UploadError(
            error,
            StatusCodes.Status400BadRequest,
            "Upload batch is empty"),
        UploadNameInvalidException error => UploadError(
            error,
            StatusCodes.Status400BadRequest,
            "Invalid upload filename"),
        UploadMalformedRequestException error => UploadError(
            error,
            StatusCodes.Status400BadRequest,
            "Malformed upload request"),
        UploadSourceReadOnlyException error => UploadError(
            error,
            StatusCodes.Status403Forbidden,
            "Source is read-only"),
        UploadNameConflictException error => UploadError(
            error,
            StatusCodes.Status409Conflict,
            "Upload filename conflict"),
        UploadFileTooLargeException error => UploadError(
            error,
            StatusCodes.Status413PayloadTooLarge,
            "Upload file is too large"),
        UploadBatchTooLargeException error => UploadError(
            error,
            StatusCodes.Status413PayloadTooLarge,
            "Upload batch is too large"),
        UploadTooManyFilesException error => UploadError(
            error,
            StatusCodes.Status413PayloadTooLarge,
            "Upload contains too many files"),
        UploadUnsupportedMediaTypeException error => UploadError(
            error,
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported upload media type"),
        UploadStorageUnavailableException error => UploadError(
            error,
            StatusCodes.Status503ServiceUnavailable,
            "Upload storage unavailable"),
        UploadCancelledException error => UploadError(
            error,
            StatusCodes.Status499ClientClosedRequest,
            "Upload cancelled"),
        UploadCleanupRequiredException error => UploadError(
            error,
            StatusCodes.Status500InternalServerError,
            "Upload cleanup required"),
        _ => new(
            StatusCodes.Status500InternalServerError,
            "Unexpected server error",
            "unexpected_error",
            "The request could not be completed."),
    };

    private static ErrorDescriptor UploadError(
        UploadException error,
        int status,
        string title) => new(status, title, error.Code, error.Detail);

    private sealed record ErrorDescriptor(int Status, string Title, string Code, string Detail);
}
