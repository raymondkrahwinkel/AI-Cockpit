using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Authentication;
using Cockpit.Core.Abstractions;
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
    ClientOAuthOptions CreateOptions(McpServerConfig server);
}

internal sealed class McpOAuthAuthorizer(ILogger<McpOAuthAuthorizer> logger) : IMcpOAuthAuthorizer, ISingletonService
{
    public ClientOAuthOptions CreateOptions(McpServerConfig server)
    {
        var options = new ClientOAuthOptions
        {
            // A fresh loopback port per server avoids collisions; the redirect is registered via DCR so a
            // dynamic port is fine. The delegate derives its listener prefix from this same RedirectUri.
            RedirectUri = new Uri($"http://127.0.0.1:{_FreeLoopbackPort()}/callback"),
            AuthorizationRedirectDelegate = _HandleAuthorizationAsync,
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

        return options;
    }

    // Opens the system browser at the authorization URL and waits on a loopback listener for the redirect,
    // returning the authorization code (or null on failure/cancel — the SDK then reports the auth failure).
    private async Task<string?> _HandleAuthorizationAsync(Uri authorizationUri, Uri redirectUri, CancellationToken cancellationToken)
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

        _OpenBrowser(authorizationUri);

        try
        {
            // Stop the listener if the connect is cancelled so GetContextAsync unblocks instead of hanging.
            using var registration = cancellationToken.Register(listener.Stop);
            var context = await listener.GetContextAsync().ConfigureAwait(false);

            var (code, error) = _ParseCallback(context.Request.Url?.Query);
            await _RespondAsync(context, error is null).ConfigureAwait(false);

            if (error is not null)
            {
                logger.LogWarning("MCP OAuth authorization returned an error: {Error}", error);
                return null;
            }

            return code;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // Listener stopped (cancelled) or torn down — treat as an aborted login.
            return null;
        }
    }

    private static (string? Code, string? Error) _ParseCallback(string? query)
    {
        string? code = null;
        string? error = null;

        foreach (var pair in (query ?? string.Empty).TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            if (key == "code")
            {
                code = value;
            }
            else if (key == "error")
            {
                error = value;
            }
        }

        if (code is null && error is null)
        {
            error = "no_code";
        }

        return (code, error);
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

    private void _OpenBrowser(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url.ToString(), UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the system browser for the MCP OAuth sign-in");
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
