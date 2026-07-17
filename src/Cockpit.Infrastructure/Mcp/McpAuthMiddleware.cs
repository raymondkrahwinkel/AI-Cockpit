using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The auth gate every cockpit-hosted MCP endpoint puts in front of its tools (AC-40): a request without this run's
/// key is turned away with a 401 before it reaches a tool. Shared by both hosts so the two loopback servers cannot
/// end up guarding themselves differently.
/// </summary>
internal static class McpAuthMiddleware
{
    public static void Require(WebApplication app, McpAuthKey authKey) =>
        app.Use(async (context, next) =>
        {
            if (!authKey.IsAuthorized(context.Request.Headers.Authorization.ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
}
