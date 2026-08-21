using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ReachCommander.Infrastructure.Authentication;

namespace ReachCommander.Api.Authentication;

public static class AuthenticationConfiguration
{
    public const string SetupPolicy = "authentication-setup";
    public const string LoginPolicy = "authentication-login";
    public const string AntiforgeryHeaderName = "X-ReachCommander-CSRF";

    public static IServiceCollection AddReachCommanderAuthentication(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services
            .AddDataProtection()
            .SetApplicationName("ReachCommander");
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>,
            AuthenticationKeyRingOptionsSetup>();

        services.AddScoped<AccountCookieEvents>();
        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = "ReachCommander.Session";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy(environment);
                options.ExpireTimeSpan = TimeSpan.FromHours(12);
                options.SlidingExpiration = true;
                options.EventsType = typeof(AccountCookieEvents);
                options.LoginPath = PathString.Empty;
                options.AccessDeniedPath = PathString.Empty;
            });

        services.AddAuthorizationBuilder().SetFallbackPolicy(
            new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        services.AddAntiforgery(options =>
        {
            options.HeaderName = AntiforgeryHeaderName;
            options.Cookie.Name = "ReachCommander.Antiforgery";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy(environment);
        });
        services.Configure<MvcOptions>(options =>
            options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                var response = context.HttpContext.Response;
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Authentication rate limit exceeded",
                    Detail = "Too many authentication attempts were submitted. Try again later.",
                    Type = "https://httpstatuses.io/429",
                    Instance = context.HttpContext.Request.Path,
                };
                problem.Extensions["code"] = "authentication_rate_limited";
                await response.WriteAsJsonAsync(
                    problem,
                    options: (System.Text.Json.JsonSerializerOptions?)null,
                    contentType: "application/problem+json",
                    cancellationToken: cancellationToken);
            };
            options.AddPolicy(SetupPolicy, context =>
                FixedWindowPartition(context, permitLimit: 5));
            options.AddPolicy(LoginPolicy, context =>
                FixedWindowPartition(context, permitLimit: 10));
        });

        return services;
    }

    private static RateLimitPartition<string> FixedWindowPartition(
        HttpContext context,
        int permitLimit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = permitLimit,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            });

    private static CookieSecurePolicy CookieSecurePolicy(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment("Testing")
            ? Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest
            : Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;
}

internal sealed class AuthenticationKeyRingOptionsSetup(
    AuthenticationDataPaths paths,
    ILoggerFactory loggerFactory) : IConfigureOptions<KeyManagementOptions>
{
    public void Configure(KeyManagementOptions options)
    {
        paths.EnsureDirectories();
        options.XmlRepository = new FileSystemXmlRepository(
            new DirectoryInfo(paths.KeysDirectory),
            loggerFactory);
    }
}
