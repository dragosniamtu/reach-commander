using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ReachCommander.Application.Authentication;

namespace ReachCommander.Api.Authentication;

public sealed class AccountCookieEvents(
    IAdministratorAccountService accountService) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var username = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var securityStamp = context.Principal?.FindFirstValue(AuthenticationClaimTypes.SecurityStamp);
        if (username is not null &&
            securityStamp is not null &&
            await accountService.ValidateSessionAsync(
                username,
                securityStamp,
                context.HttpContext.RequestAborted))
        {
            return;
        }

        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(context.Scheme.Name);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
