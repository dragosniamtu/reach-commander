using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.SourceManagement;

namespace ReachCommander.Api.Errors;

public sealed class SourceManagementExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<SourceManagementExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not SourceManagementException sourceException)
        {
            return false;
        }

        var status = StatusFor(sourceException.Code);
        logger.LogInformation(
            "Source-management request failed with {ErrorCode} and {ExceptionType}.",
            sourceException.Code,
            exception.GetType().Name);
        httpContext.Response.StatusCode = status;
        var details = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(sourceException.Code),
            Detail = sourceException.PublicDetail,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path,
        };
        details.Extensions["code"] = sourceException.Code;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = details,
            Exception = exception,
        });
    }

    private static int StatusFor(string code) => code switch
    {
        "source_management_validation_failed" or "untrusted_source_ancestry" =>
            StatusCodes.Status400BadRequest,
        "source_management_busy" or "source_management_blocked_by_operations" =>
            StatusCodes.Status409Conflict,
        "source_management_failed" => StatusCodes.Status500InternalServerError,
        _ => StatusCodes.Status503ServiceUnavailable,
    };

    private static string TitleFor(string code) => code switch
    {
        "source_management_installer_upgrade_required" => "Installer upgrade required",
        "source_management_busy" => "Source management busy",
        "source_management_blocked_by_operations" => "Source management blocked",
        "source_management_validation_failed" => "Source folder not accepted",
        "untrusted_source_ancestry" => "Source folder parents are not trusted",
        "source_management_failed" => "Source management failed",
        _ => "Source management unavailable",
    };
}
