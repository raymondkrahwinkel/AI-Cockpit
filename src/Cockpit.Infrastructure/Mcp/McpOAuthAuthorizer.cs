using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Builds the <see cref="ClientOAuthOptions"/> for an OAuth-protected remote MCP server (#26). The MCP
/// client's built-in <c>ClientOAuthProvider</c> drives discovery, PKCE (S256), optional Dynamic Client
/// Registration and token refresh once a 401 is intercepted; all this class supplies is the desktop
/// authorization step — a loopback <see cref="HttpListener"/> that catches the redirect while the system
/// browser handles the user's login, mirroring the official ProtectedMcpClient sample.
/// </summary>
internal interface IMcpOAuthAuthorizer
{
    /// <summary>
    /// The OAuth options for <paramref name="server"/>. When <paramref name="interactive"/> is
    /// <see langword="false"/> the authorization step refuses instead of opening a browser, so a caller that is not
    /// the operator — starting a session, renewing a stale token — can get as far as the refresh grant and no
    /// further (AC-353).
    /// <para>
    /// A caller that has to report the outcome to the operator passes a <paramref name="stageRecorder"/> and is told
    /// how far the authorization got (AC-457); one that only needs the credential leaves it out.
    /// </para>
    /// </summary>
    ClientOAuthOptions CreateOptions(McpServerConfig server, bool interactive = true, McpSignInStageRecorder? stageRecorder = null);
}

internal sealed class McpOAuthAuthorizer(ILogger<McpOAuthAuthorizer> logger, IMcpOAuthTokenStore tokenStore)
    : IMcpOAuthAuthorizer, ISingletonService
{
    /// <summary>
    /// Stands in for the hand-off to the desktop. Null in production, where <see cref="_OpenBrowser"/> is the only
    /// part of this class that reaches outside the process — and the only part a test cannot exercise without a
    /// browser window appearing on the machine running the suite.
    /// </summary>
    internal Func<Uri, bool>? BrowserOpener { get; init; }

    public ClientOAuthOptions CreateOptions(McpServerConfig server, bool interactive = true, McpSignInStageRecorder? stageRecorder = null)
    {
        var options = new ClientOAuthOptions
        {
            // A fresh loopback port per server avoids collisions; the redirect is registered via DCR so a
            // dynamic port is fine. The delegate derives its listener prefix from this same RedirectUri.
            RedirectUri = new Uri($"http://127.0.0.1:{_FreeLoopbackPort()}/callback"),
            // AuthorizationCallbackHandler, not the obsolete AuthorizationRedirectDelegate (MCP9007): the
            // latter cannot carry `state`/`iss` back to the SDK, which is what lets it validate the redirect
            // against the request it made (RFC 9207 mix-up mitigation).
            AuthorizationCallbackHandler = interactive
                ? (context, cancellationToken) =>
                    _HandleAuthorizationAsync(context.AuthorizationUri, context.RedirectUri, stageRecorder, cancellationToken)
                : _RefuseAuthorizationAsync,

            // Without this the SDK caches the token with the transport and the cockpit never sees it: the sign-in
            // would work and then be thrown away with the connection. Storing it is what lets one login serve the
            // spawned agents too, and survive a restart.
            TokenCache = new McpOAuthTokenCache(server.IdentityKey, server.Name, server.Url, tokenStore),
        };

        // A configured client id takes precedence; otherwise let the server register us dynamically (RFC 7591).
        if (!string.IsNullOrWhiteSpace(server.OAuthClientId))
        {
            options.ClientId = server.OAuthClientId;
        }
        else
        {
            options.DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "AI-OS Cockpit" };
        }

        // The escape hatch (AC-505): ClientOAuthOptions.Scopes is only ever a fallback the SDK uses when a server
        // gives it nothing to derive from, so it cannot override a server that advertises its own (narrower or
        // wider) scopes_supported — which is exactly the case a per-server operator override exists for. Replacing
        // the candidate list via ScopeSelector, which runs after that derivation, is what actually overrides it.
        if (!string.IsNullOrWhiteSpace(server.OAuthScopes))
        {
            // Split on whitespace or comma: the field is free text, and a scope list pasted from a server's own
            // docs is at least as often comma-separated as space-separated. The authorization request itself
            // always joins back with a plain space (OAuth's own separator), regardless of what was typed.
            var configuredScopes = server.OAuthScopes.Split(
                [' ', ',', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);
            options.ScopeSelector = _ => configuredScopes;
        }

        return options;
    }

    /// <summary>
    /// The non-interactive authorization step: there is nobody to log in, so it declines. Returning no code makes
    /// the SDK report the authorization as failed, which the coordinator reads as "this needs the operator" — the
    /// point being that starting a session must never make a browser window appear on its own.
    /// </summary>
    private Task<AuthorizationResult?> _RefuseAuthorizationAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation("An MCP server needs an interactive sign-in, which was not requested here; leaving it unauthorized.");
        return Task.FromResult<AuthorizationResult?>(null);
    }

    // Opens the system browser at the authorization URL and waits on a loopback listener for the redirect,
    // returning the authorization code (or null on failure/cancel — the SDK then reports the auth failure).
    // Each stage is recorded where it is reached and never in advance (AC-457): the operator is told which stage
    // stopped, so a stage noted before the thing happened would put the untruth back one layer down.
    private async Task<AuthorizationResult?> _HandleAuthorizationAsync(
        Uri authorizationUri,
        Uri redirectUri,
        McpSignInStageRecorder? stageRecorder,
        CancellationToken cancellationToken)
    {
        var prefix = redirectUri.GetLeftPart(UriPartial.Authority);
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            logger.LogWarning(ex, "Could not open the loopback listener at {Prefix} for the MCP OAuth redirect", prefix);
            return null;
        }

        // Nothing took the URL, so no redirect can ever arrive. Waiting on the listener anyway would leave the
        // operator on a spinner with no message at all, which is worse than the wrong message this ticket removes.
        if (!(BrowserOpener ?? _OpenBrowser)(authorizationUri))
        {
            return null;
        }

        stageRecorder?.Record(McpSignInStage.BrowserRequested);

        try
        {
            // Stop the listener if the connect is cancelled so GetContextAsync unblocks instead of hanging.
            using var registration = cancellationToken.Register(listener.Stop);
            var context = await listener.GetContextAsync().ConfigureAwait(false);

            // Recorded on arrival, not on success: a redirect carrying error=access_denied came back just as much as
            // one carrying a code, and this listener answers the operator's browser tab either way. Waiting until
            // the code is in hand would tell someone who watched their own browser return that nothing came back.
            stageRecorder?.Record(McpSignInStage.AuthorizationReturned);

            var (code, state, iss, error) = _ParseCallback(context.Request.Url?.Query);
            await _RespondAsync(context, error is null).ConfigureAwait(false);

            if (error is not null)
            {
                logger.LogWarning("MCP OAuth authorization returned an error: {Error}", error);
                return null;
            }

            return new AuthorizationResult { Code = code, State = state, Iss = iss };
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // Listener stopped (cancelled) or torn down — treat as an aborted login.
            return null;
        }
    }

    private static (string? Code, string? State, string? Iss, string? Error) _ParseCallback(string? query)
    {
        string? code = null;
        string? state = null;
        string? iss = null;
        string? error = null;

        foreach (var pair in (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            switch (key)
            {
                case "code":
                    code = value;
                    break;
                case "state":
                    state = value;
                    break;
                case "iss":
                    iss = value;
                    break;
                case "error":
                    error = value;
                    break;
            }
        }

        if (code is null && error is null)
        {
            error = "no_code";
        }

        return (code, state, iss, error);
    }

    private static async Task _RespondAsync(HttpListenerContext context, bool success)
    {
        var message = success
            ? "Signed in to the MCP server. You can close this tab and return to Cockpit."
            : "Sign-in failed or was cancelled. You can close this tab and return to Cockpit.";
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html><body style=\"font-family:sans-serif\">{message}</body></html>");

        context.Response.ContentType = "text/html";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    // Answers whether the URL was handed over at all — not whether a window appeared, which this process cannot see.
    // A scheme this refuses to give the shell, or a desktop with nothing on PATH to open one, throws or returns
    // early; a handler that then declines the URL does so out of reach, and is why the stage says "requested".
    private bool _OpenBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the system browser for the MCP OAuth sign-in");
            return false;
        }
    }

    // Grab an ephemeral free loopback port by binding :0, reading the assigned port, then releasing it. A
    // brief race with another process is possible but harmless — the login just fails and can be retried.
    private static int _FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
