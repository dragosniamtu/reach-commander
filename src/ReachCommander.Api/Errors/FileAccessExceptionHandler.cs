using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.Files;
using ReachCommander.Application.Sources;
using ReachCommander.Application.SystemMetrics;
using ReachCommander.Application.Uploads;
using ReachCommander.Application.BatchRenames;
using ReachCommander.Application.Archives;

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
        InvalidRenameRuleException error => RenameError(
            error,
            StatusCodes.Status400BadRequest,
            "Invalid rename rule",
            "invalid_rename_rule"),
        BatchTooLargeException error => RenameError(
            error,
            StatusCodes.Status400BadRequest,
            "Rename batch is too large",
            "batch_too_large"),
        SourceReadOnlyException error => RenameError(
            error,
            StatusCodes.Status403Forbidden,
            "Source is read-only",
            "source_read_only"),
        RenamePlanNotFoundException error => RenameError(
            error,
            StatusCodes.Status404NotFound,
            "Rename plan not found",
            "rename_plan_not_found"),
        RenamePlanExpiredException error => RenameError(
            error,
            StatusCodes.Status410Gone,
            "Rename plan expired",
            "rename_plan_expired"),
        RenamePlanStaleException error => RenameError(
            error,
            StatusCodes.Status409Conflict,
            "Rename plan is stale",
            "rename_plan_stale"),
        RenameRecoveryRequiredException error => RenameError(
            error,
            StatusCodes.Status500InternalServerError,
            "Rename recovery required",
            "rename_recovery_required"),
        ArchiveUnsupportedException error => ArchiveError(
            error,
            StatusCodes.Status415UnsupportedMediaType,
            "Unsupported archive"),
        ArchiveInvalidException error => ArchiveError(
            error,
            StatusCodes.Status400BadRequest,
            "Invalid archive"),
        ArchiveEncryptedException error => ArchiveError(
            error,
            StatusCodes.Status422UnprocessableEntity,
            "Encrypted archive"),
        ArchiveVolumeSecondaryException error => ArchiveError(
            error,
            StatusCodes.Status409Conflict,
            "Secondary archive volume"),
        ArchiveVolumeSetInvalidException error => ArchiveError(
            error,
            StatusCodes.Status422UnprocessableEntity,
            "Invalid archive volume set"),
        ArchiveEntryUnsafeException error => ArchiveError(
            error,
            StatusCodes.Status422UnprocessableEntity,
            "Unsafe archive entry"),
        ArchiveLimitExceededException error => ArchiveError(
            error,
            StatusCodes.Status413PayloadTooLarge,
            "Archive limit exceeded"),
        ArchiveDestinationInvalidException error => ArchiveError(
            error,
            StatusCodes.Status400BadRequest,
            "Invalid archive extraction destination"),
        ArchiveDestinationReadOnlyException error => ArchiveError(
            error,
            StatusCodes.Status403Forbidden,
            "Archive extraction destination is read-only"),
        ArchiveDestinationConflictException error => ArchiveError(
            error,
            StatusCodes.Status409Conflict,
            "Archive extraction destination conflict"),
        ArchivePlanNotFoundException error => ArchiveError(
            error,
            StatusCodes.Status404NotFound,
            "Archive extraction plan not found"),
        ArchivePlanExpiredException error => ArchiveError(
            error,
            StatusCodes.Status410Gone,
            "Archive extraction plan expired"),
        ArchivePlanStaleException error => ArchiveError(
            error,
            StatusCodes.Status409Conflict,
            "Archive extraction plan is stale"),
        ArchiveDestinationChangedException error => ArchiveError(
            error,
            StatusCodes.Status409Conflict,
            "Archive extraction destination changed"),
        ArchiveCapacityReachedException error => ArchiveError(
            error,
            StatusCodes.Status429TooManyRequests,
            "Archive extraction capacity reached"),
        ArchiveWorkerFailedException error => ArchiveError(
            error,
            StatusCodes.Status500InternalServerError,
            "Archive worker failed"),
        ArchiveExtractionCancelledException error => ArchiveError(
            error,
            StatusCodes.Status499ClientClosedRequest,
            "Archive extraction cancelled"),
        ArchiveRecoveryRequiredException error => ArchiveError(
            error,
            StatusCodes.Status500InternalServerError,
            "Archive extraction recovery required"),
        BadHttpRequestException error when error.StatusCode == StatusCodes.Status413PayloadTooLarge => new(
            StatusCodes.Status413PayloadTooLarge,
            "Request body too large",
            "request_too_large",
            "The request body exceeds the allowed size."),
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

    private static ErrorDescriptor RenameError(
        BatchRenameException error,
        int status,
        string title,
        string code) => new(status, title, code, error.Message);

    private static ErrorDescriptor ArchiveError(
        ArchiveException error,
        int status,
        string title) => new(status, title, error.Code, error.Detail);

    private sealed record ErrorDescriptor(int Status, string Title, string Code, string Detail);
}
