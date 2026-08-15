using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Infrastructure.Mcp;

// The auth gate every cockpit-hosted MCP endpoint puts in front of its tools (AC-40): a request without a valid key
// is turned away with a 401 before it reaches a tool. Shared by both hosts so the two loopback servers cannot end up
// guarding themselves differently.
//
// AC-790 split this into two trust boundaries rather than one shared credential pool: on loopback, a valid key is
// the shared app-lifetime key (the in-process tool loop, and any session not yet on a per-session token) or a
// per-session token from the `SessionMcpKeyring` (AC-89) — both meant to never leave this machine. On the optional
// off-loopback listener, the *only* valid credential is the persistent node shared secret a second Cockpit was
// handed to add this instance as a plain MCP server; the loopback-scoped key and session tokens are rejected there
// even if presented, so a leak of one boundary's credential cannot be replayed against the other. `IsHttps` is the
// discriminator because `CockpitMcpEndpointHost` only ever puts TLS on the off-loopback listener — the loopback one
// stays plain HTTP — so it is the request's real trust boundary, not an assumption about listener order.
//
// A per-session token additionally names the session: the middleware stamps that verified pane id onto
// `McpRequestContext` for the request's async flow, so the consent broker scopes on the session the request
// actually came from rather than on the value the agent declared. The shared key and the node secret name no
// session (the identity stays null), so those callers keep their previous consent behaviour.
internal static class McpAuthMiddleware
{
    public static void Require(WebApplication app, McpAuthKey authKey, SessionMcpKeyring keyring, string? nodeSharedSecret = null) =>
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : header;

            if (context.Request.IsHttps)
            {
                // The node's own persistent shared secret (AC-790), and nothing else: this socket is reachable off
                // this machine, so a loopback-scoped credential must not work here even if it happens to match.
                if (!string.IsNullOrEmpty(nodeSharedSecret) && _ConstantTimeEquals(token, nodeSharedSecret))
                {
                    McpRequestContext.Set(null);
                    await next(context).ConfigureAwait(false);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // The shared app key: authorized, but names no session (verified identity stays null).
            if (authKey.IsAuthorized(header))
            {
                McpRequestContext.Set(null);
                await next(context).ConfigureAwait(false);
                return;
            }

            // Otherwise it must be a live per-session token; if so, the request is attributed to that pane.
            if (keyring.PaneFor(token) is { } paneId)
            {
                McpRequestContext.Set(paneId);
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

    // Constant-time for the same reason McpAuthKey.IsAuthorized is — this credential crosses a real network, not
    // just a local socket, so a timing side-channel is a real leak here rather than a theoretical one.
    private static bool _ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
