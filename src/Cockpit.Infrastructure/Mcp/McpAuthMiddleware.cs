using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-790/AC-791: auth gate shared by every host — loopback accepts the app key or a per-session token (AC-89),
// the off-loopback listener only the node's shared secret; a node refusal is a classifiable 401 (AC-247/DEP-172).
// AC-1148: `authorize` then decides on that identity before any tool — 401 is "who?", 403 "not you", and required.
internal static class McpAuthMiddleware
{
    // `nodeSharedSecret` is a holder, not a value (AC-792): pairing mints a new secret and unpairing removes one
    // while this process runs, and a copy captured at mount time would keep letting an unpaired controller in until
    // the next launch. Revocation that waits for a restart is not revocation.
    public static void Require(
        WebApplication app,
        McpAuthKey authKey,
        SessionMcpKeyring keyring,
        Func<string?, ValueTask<bool>> authorize,
        NodeSharedSecret? nodeSharedSecret = null,
        ConnectKeyVerifier? connectKeys = null) =>
        app.Use(async (context, next) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : header;

            if (context.Request.IsHttps)
            {
                // IPv4 over a dual-stack socket arrives mapped into IPv6; unmapped, one machine is one lockout bucket.
                var remoteIp = context.Connection.RemoteIpAddress;
                var remoteAddress = (remoteIp is { IsIPv4MappedToIPv6: true } ? remoteIp.MapToIPv4() : remoteIp)?.ToString() ?? "unknown";

                // The node's own persistent shared secret (AC-790) or a connect key (AC-1351), and nothing else:
                // this socket is reachable off this machine, so a loopback-scoped credential must not work here even
                // if it happens to match. Read now rather than closed over, so the answer follows the current pairing.
                var caller = connectKeys is null
                    ? (nodeSharedSecret?.Value is { } secret && _ConstantTimeEquals(token, secret) ? NodeCaller.ForPairing(remoteAddress) : null)
                    : await connectKeys.AuthenticateAsync(token, nodeSharedSecret?.Value, remoteAddress).ConfigureAwait(false);

                // One refusal for every way in which this failed — see `ConnectKeyVerifier`.
                if (caller is null)
                {
                    await _RefuseWithReasonAsync(context).ConfigureAwait(false);
                    return;
                }

                // A revoked key's requests still in flight end with it (AC-1351, DEP-208's lesson).
                using var revocation = caller.Revoked.Register(context.Abort);
                await _DispatchIfAuthorizedAsync(context, next, authorize, NodeCallerIdentity.PaneId, caller).ConfigureAwait(false);
                return;
            }

            // The shared app key: authorized, but names no session (verified identity stays null).
            if (authKey.IsAuthorized(header))
            {
                await _DispatchIfAuthorizedAsync(context, next, authorize, null).ConfigureAwait(false);
                return;
            }

            // Otherwise it must be a live per-session token; if so, the request is attributed to that pane.
            if (keyring.PaneFor(token) is { } paneId)
            {
                await _DispatchIfAuthorizedAsync(context, next, authorize, paneId).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        });

    // AC-1148: the identity is stamped either way, so a refusal is attributable; only the dispatch is withheld.
    // The body names no policy source — a caller learns it may not be here, and nothing about what it missed.
    private static async Task _DispatchIfAuthorizedAsync(HttpContext context, RequestDelegate next, Func<string?, ValueTask<bool>> authorize, string? paneId, NodeCaller? nodeCaller = null)
    {
        McpRequestContext.Set(paneId, nodeCaller);
        if (!await authorize(paneId).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                """{"error":"forbidden","error_description":"This cockpit endpoint is not available to this caller."}""")
                .ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    // AC-791: written out rather than a bare status code so a controller can tell it from silence — see the class
    // comment.
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
    // just a local socket. AC-1351: over both sides' hashes, so the length of the secret does not leak either.
    private static bool _ConstantTimeEquals(string a, string b) => ConnectKeyVerifier.ConstantTimeEquals(a, b);
}
