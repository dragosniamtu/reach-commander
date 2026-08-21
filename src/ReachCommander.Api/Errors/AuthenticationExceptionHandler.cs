using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.Authentication;

namespace ReachCommander.Api.Errors;

public sealed class AuthenticationExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<AuthenticationExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not AuthenticationException authenticationException)
        {
            return false;
        }

        var descriptor = Describe(authenticationException);
        logger.LogInformation(
            "Authentication request failed with {ErrorCode} at {RequestPath}",
            descriptor.Code,
            httpContext.Request.Path);

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

    private static ErrorDescriptor Describe(AuthenticationException exception) => exception switch
    {
        AuthenticationValidationException => new(
            StatusCodes.Status400BadRequest,
            "Invalid authentication request",
            exception.Code,
            exception.Detail),
        AdministratorAlreadyExistsException => new(
            StatusCodes.Status409Conflict,
            "Administrator already configured",
            exception.Code,
            "The administrator account has already been configured."),
        InvalidSetupCodeException => InvalidCredentials("setup_failed"),
        InvalidCurrentPasswordException => InvalidCredentials("invalid_credentials"),
        AuthenticationStateUnavailableException => new(
            StatusCodes.Status503ServiceUnavailable,
            "Authentication unavailable",
            exception.Code,
            "Authentication state is temporarily unavailable."),
        _ => new(
            StatusCodes.Status500InternalServerError,
            "Authentication failed",
            "authentication_failed",
            "The authentication request could not be completed."),
    };

    private static ErrorDescriptor InvalidCredentials(string code) => new(
        StatusCodes.Status401Unauthorized,
        "Authentication failed",
        code,
        "The supplied credentials are not valid.");

    private sealed record ErrorDescriptor(int Status, string Title, string Code, string Detail);
}
