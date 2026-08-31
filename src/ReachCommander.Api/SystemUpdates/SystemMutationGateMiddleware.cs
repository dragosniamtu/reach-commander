using Microsoft.AspNetCore.Mvc;
using ReachCommander.Application.SystemUpdates;

namespace ReachCommander.Api.SystemUpdates;

public sealed class SystemMutationGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISystemMutationGate gate)
    {
        if (!IsMutation(context.Request))
        {
            await next(context);
            return;
        }

        var lease = gate.TryEnter();
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "System update in progress",
                detail: "Mutating requests are temporarily unavailable while ReachCommander updates.",
                type: "https://httpstatuses.io/503",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "system_update_in_progress",
                }).ExecuteAsync(context);
            return;
        }

        await using (lease)
        {
            await next(context);
        }
    }

    private static bool IsMutation(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") &&
        !request.Path.StartsWithSegments("/api/system-update") &&
        !IsSourceManagementAddPath(request.Path) &&
        request.Method is "POST" or "PUT" or "PATCH" or "DELETE";

    private static bool IsSourceManagementAddPath(PathString path) =>
        path.StartsWithSegments(
            "/api/source-management/sources",
            StringComparison.OrdinalIgnoreCase,
            out var remaining) &&
        (remaining == PathString.Empty || remaining == "/");
}
