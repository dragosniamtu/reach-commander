using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.MediaPreviews;

namespace ReachCommander.Api.Errors;

public sealed class MediaPreviewExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<MediaPreviewExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not MediaPreviewException mediaError)
        {
            return false;
        }

        var status = StatusFor(mediaError.Code);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                "Media preview failed with {ErrorCode}, {ExceptionType}, and HResult {HResult}.",
                mediaError.Code,
                exception.GetType().Name,
                exception.HResult);
        }
        else
        {
            logger.LogInformation(
                "Media preview request failed with {ErrorCode} and {ExceptionType}.",
                mediaError.Code,
                exception.GetType().Name);
        }

        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(mediaError.Code),
            Detail = mediaError.PublicDetail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
        };
        details.Extensions["code"] = mediaError.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static int StatusFor(string code) => code switch
    {
        "preview_session_not_found" or "subtitle_save_plan_not_found" =>
            StatusCodes.Status404NotFound,
        "preview_session_expired" or "subtitle_save_plan_expired" =>
            StatusCodes.Status410Gone,
        "preview_session_stale" or "subtitle_save_plan_stale" =>
            StatusCodes.Status409Conflict,
        "subtitle_source_read_only" => StatusCodes.Status403Forbidden,
        "video_format_unsupported" or "subtitle_encoding_unsupported" =>
            StatusCodes.Status415UnsupportedMediaType,
        "preview_capacity_reached" => StatusCodes.Status429TooManyRequests,
        "media_tools_unavailable" => StatusCodes.Status503ServiceUnavailable,
        "subtitle_save_failed" or "subtitle_recovery_required" =>
            StatusCodes.Status500InternalServerError,
        "video_invalid" or
        "symbolic_link_rejected" or
        "subtitle_invalid" or
        "subtitle_too_large" or
        "subtitle_offset_invalid" or
        "subtitle_selection_invalid" or
        "subtitle_missing" or
        "subtitle_backup_unavailable" or
        "media_probe_failed" or
        "media_transcode_failed" or
        "hls_asset_invalid" => StatusCodes.Status422UnprocessableEntity,
        "preview_not_ready" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string TitleFor(string code) => code switch
    {
        "video_format_unsupported" => "Unsupported video format",
        "preview_session_not_found" => "Media preview not found",
        "preview_session_expired" => "Media preview expired",
        "preview_session_stale" => "Media preview is stale",
        "preview_not_ready" => "Media preview not ready",
        "preview_capacity_reached" => "Media preview capacity reached",
        "media_tools_unavailable" => "Media tools unavailable",
        "subtitle_source_read_only" => "Subtitle source is read-only",
        "subtitle_save_plan_not_found" => "Subtitle save plan not found",
        "subtitle_save_plan_expired" => "Subtitle save plan expired",
        "subtitle_save_plan_stale" => "Subtitle save plan is stale",
        "subtitle_recovery_required" => "Subtitle recovery required",
        _ => "Media preview request failed",
    };
}
