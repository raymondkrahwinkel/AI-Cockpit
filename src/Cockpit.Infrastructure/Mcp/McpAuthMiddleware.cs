using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Cockpit.Core.Mcp;

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
// actually came from rather than on the value the agent declared. The shared key names no session (the identity
// stays null), so the in-process tool loop keeps its previous consent behaviour.
//
// AC-791: the node secret does name one — `NodeCallerIdentity.PaneId`, the single remote-caller role, which is
// where that whole authorization model is written down. It is an identity rather than null so that a caller from
// another machine cannot share the null bucket the local tool loop sits in, and so that every tool keying on the
// verified pane fails closed for it instead of falling back to a pane id the caller declared.
//
// A refusal on the node listener says so in a form a caller can classify (AC-791, criterion 3): a 401 with an
// RFC 6750 `WWW-Authenticate: Bearer error="invalid_token"` challenge and a small JSON body carrying the same
// code. That is what makes "the cockpit turned me away" distinguishable at the other end from "nothing answered"
// (a transport failure, no status at all) and from "that tool does not exist" (a 200 carrying a JSON-RPC error) —
// the AC-247/DEP-172 lesson that a refusal without a code or a text cannot be classified. It stays deliberately
// generic about *why*: a missing credential and a wrong one get the same answer, so the response never becomes an
// oracle for probing which of the two it was.
//
// Only on that boundary, though — loopback keeps the bare 401 it always returned. A `WWW-Authenticate: Bearer`
// challenge is what the MCP specification has a client read as "this server wants OAuth", so putting one on the
// loopback answer would invite a local client whose session token has just been revoked into a discovery flow
// against endpoints that have no OAuth at all (`McpOAuthProxyHost` shares this middleware). The controller is the
// party that needed to tell a refusal apart; a local session is not, and it keeps its previous behaviour exactly.
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
                    McpRequestContext.Set(NodeCallerIdentity.PaneId);
                    await next(context).ConfigureAwait(false);
                    return;
                }

                await _RefuseWithReasonAsync(context).ConfigureAwait(false);
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

    // The node listener's refusal, written out rather than left as a bare status code so a controller can tell it
    // from silence — see the class comment for why this shape and why it stops at this boundary. The challenge
    // header is the standard form; the body carries the same code because an MCP client surfaces a response body
    // far more readily than a header.
    private static Task _RefuseWithReasonAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate =
            "Bearer error=\"invalid_token\", error_description=\"The cockpit did not accept this bearer token.\"";
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(
            """{"error":"invalid_token","error_description":"The cockpit did not accept this bearer token."}""");
    }

    // Constant-time for the same reason McpAuthKey.IsAuthorized is — this credential crosses a real network, not
    // just a local socket, so a timing side-channel is a real leak here rather than a theoretical one.
    private static bool _ConstantTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
