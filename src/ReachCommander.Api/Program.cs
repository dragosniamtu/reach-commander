using System.Text.Json;
using System.Text.Json.Serialization;
using ReachCommander.Api.Errors;
using ReachCommander.Api.Uploads;
using ReachCommander.Application.Sources;
using ReachCommander.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FileAccessExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddTransient<MultipartUploadReader>();
builder.Services.AddReachCommanderInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapHealthChecks("/health");
app.Map("/api/{**unmatched}", () => Results.Problem(
    statusCode: StatusCodes.Status404NotFound,
    title: "API route not found",
    extensions: new Dictionary<string, object?> { ["code"] = "route_not_found" }));
app.MapFallbackToFile("index.html");

await app.Services
    .GetRequiredService<ISourceCatalog>()
    .GetDefinitionsAsync(CancellationToken.None);

app.Run();

public partial class Program;
