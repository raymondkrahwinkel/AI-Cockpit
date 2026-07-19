using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// The auth gate every cockpit-hosted MCP endpoint puts in front of its tools (AC-40): a request without a valid key
/// is turned away with a 401 before it reaches a tool. Shared by both hosts so the two loopback servers cannot end up
/// guarding themselves differently.
/// <para>
/// A valid key is either the shared app-lifetime key (the in-process tool loop, and any session not yet on a
/// per-session token) or a per-session token from the <see cref="SessionMcpKeyring"/> (AC-89). A per-session token
/// additionally names the session: the middleware stamps that verified pane id onto <see cref="McpRequestContext"/>
/// for the request's async flow, so the consent broker scopes on the session the request actually came from rather
/// than on the value the agent declared. The shared key names no session (the identity stays null), so those callers
/// keep their previous consent behaviour.
/// </para>
/// </summary>
internal static class McpAuthMiddleware
{
    public static void Require(WebApplication app, McpAuthKey authKey, SessionMcpKeyring keyring) =>
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers.Authorization.ToString();

            // The shared app key: authorized, but names no session (verified identity stays null).
            if (authKey.IsAuthorized(header))
            {
                McpRequestContext.Set(null);
                await next(context).ConfigureAwait(false);
                return;
            }

            // Otherwise it must be a live per-session token; if so, the request is attributed to that pane.
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : header;
            if (keyring.PaneFor(token) is { } paneId)
            {
                McpRequestContext.Set(paneId);
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });
}
