using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ReachCommander.Api.Authentication;
using ReachCommander.Api.Contracts.Authentication;
using ReachCommander.Application.Authentication;

namespace ReachCommander.Api.Controllers;

[ApiController]
[Route("api/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthenticationController(
    IAdministratorAccountService accountService,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("session")]
    [AllowAnonymous]
    [ProducesResponseType<AuthSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthSessionResponse>> GetSession(
        CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return Ok(AuthenticatedSession(User.FindFirstValue(ClaimTypes.NameIdentifier)!));
        }

        var state = await accountService.GetStateAsync(cancellationToken);
        return Ok(state == AdministratorAccountState.SetupRequired
            ? new AuthSessionResponse("setupRequired", null)
            : new AuthSessionResponse("anonymous", null));
    }

    [HttpGet("antiforgery")]
    [AllowAnonymous]
    [ProducesResponseType<AntiforgeryTokenResponse>(StatusCodes.Status200OK)]
    public ActionResult<AntiforgeryTokenResponse> GetAntiforgeryToken()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new AntiforgeryTokenResponse(tokens.RequestToken!));
    }

    [HttpPost("setup")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthenticationConfiguration.SetupPolicy)]
    [ProducesResponseType<AuthSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthSessionResponse>> Setup(
        SetupAdministratorRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await accountService.CreateAsync(
            new(request.SetupCode, request.Username, request.Password),
            cancellationToken);
        await SignInAsync(identity);
        return Ok(AuthenticatedSession(identity.Username));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(AuthenticationConfiguration.LoginPolicy)]
    [ProducesResponseType<AuthSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthSessionResponse>> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var identity = await accountService.AuthenticateAsync(
            request.Username,
            request.Password,
            cancellationToken);
        if (identity is null)
        {
            return InvalidCredentials();
        }

        await SignInAsync(identity);
        return Ok(AuthenticatedSession(identity.Username));
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpPost("password")]
    [ProducesResponseType<AuthSessionResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthSessionResponse>> ChangePassword(
        ChangePasswordRequest request,
        CancellationToken cancellationToken)
    {
        var username = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var identity = await accountService.ChangePasswordAsync(
            new(username, request.CurrentPassword, request.NewPassword),
            cancellationToken);
        await SignInAsync(identity);
        return Ok(AuthenticatedSession(identity.Username));
    }

    private async Task SignInAsync(AdministratorIdentity identity)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.Username),
            new Claim(ClaimTypes.Name, identity.Username),
            new Claim(AuthenticationClaimTypes.SecurityStamp, identity.SecurityStamp),
        };
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                AllowRefresh = true,
                IsPersistent = false,
            });
    }

    private ActionResult<AuthSessionResponse> InvalidCredentials()
    {
        var details = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = "Invalid credentials",
            Detail = "The supplied credentials are not valid.",
            Type = "https://httpstatuses.io/401",
            Instance = HttpContext.Request.Path,
        };
        details.Extensions["code"] = "invalid_credentials";
        return Unauthorized(details);
    }

    private static AuthSessionResponse AuthenticatedSession(string username) =>
        new("authenticated", username);
}
