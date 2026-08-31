using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ReachCommander.Api;
using ReachCommander.Api.Authentication;
using ReachCommander.Api.Errors;
using ReachCommander.Api.Uploads;
using ReachCommander.Application.Sources;
using ReachCommander.Infrastructure;
using ReachCommander.Api.SystemUpdates;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllersWithViews()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AuthenticationExceptionHandler>();
builder.Services.AddExceptionHandler<SystemUpdateExceptionHandler>();
builder.Services.AddExceptionHandler<SourceManagementExceptionHandler>();
builder.Services.AddExceptionHandler<FileOperationExceptionHandler>();
builder.Services.AddExceptionHandler<FileAccessExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddTransient<MultipartUploadReader>();
builder.Services.AddReachCommanderReverseProxy(builder.Configuration);
builder.Services.AddReachCommanderInfrastructure(builder.Configuration);
builder.Services.AddReachCommanderAuthentication(builder.Configuration, builder.Environment);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<SystemMutationGateMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();
app.Map("/api/{**unmatched}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API route not found",
    extensions: new Dictionary<string, object?> { ["code"] = "route_not_found" }))
    .RequireAuthorization();
app.MapFallbackToFile("index.html").AllowAnonymous();

await app.Services
    .GetRequiredService<ISourceCatalog>()
    .GetDefinitionsAsync(CancellationToken.None);

app.Run();

public partial class Program;
