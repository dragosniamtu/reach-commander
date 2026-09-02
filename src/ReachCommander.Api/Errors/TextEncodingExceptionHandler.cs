using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.TextEncodings;

namespace ReachCommander.Api.Errors;

public sealed class TextEncodingExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<TextEncodingExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not TextEncodingException encodingException)
        {
            return false;
        }

        var status = StatusFor(encodingException.Code);
        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                "Text encoding request failed with {ErrorCode}, {ExceptionType}, and HResult {HResult}.",
                encodingException.Code,
                exception.GetType().Name,
                exception.HResult);
        }
        else
        {
            logger.LogInformation(
                "Text encoding request failed with {ErrorCode} and {ExceptionType}.",
                encodingException.Code,
                exception.GetType().Name);
        }

        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(encodingException.Code),
            Detail = encodingException.PublicDetail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
        };
        details.Extensions["code"] = encodingException.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static int StatusFor(string code) => code switch
    {
        "text_encoding_plan_not_found" or "text_encoding_operation_not_found" =>
            StatusCodes.Status404NotFound,
        "text_encoding_plan_expired" or "text_encoding_operation_expired" =>
            StatusCodes.Status410Gone,
        "text_encoding_capacity_reached" => StatusCodes.Status429TooManyRequests,
        "text_encoding_invalid_request" or
        "text_encoding_invalid_source" or
        "text_encoding_invalid_output" => StatusCodes.Status422UnprocessableEntity,
        "text_encoding_operation_failed" or
        "text_encoding_recovery_required" => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status400BadRequest,
    };

    private static string TitleFor(string code) => code switch
    {
        "text_encoding_plan_not_found" => "Encoding preview not found",
        "text_encoding_plan_expired" => "Encoding preview expired",
        "text_encoding_operation_not_found" => "Encoding operation not found",
        "text_encoding_operation_expired" => "Encoding operation expired",
        "text_encoding_capacity_reached" => "Encoding capacity reached",
        "text_encoding_recovery_required" => "Encoding recovery required",
        _ => "Text encoding request failed",
    };
}
