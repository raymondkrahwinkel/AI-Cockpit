using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Mcp;

// The desktop's step of an OAuth sign-in: the system browser, and a loopback listener that catches the redirect and
// answers the tab. AC-1571 B2: the listener opens before the server is told its port, and only the redirect on
// /callback carrying the state of the link it was given ends the wait; anything else on that port is turned away.
internal sealed class McpOAuthLoopback : IDisposable
{
    private readonly HttpListener _listener;

    private McpOAuthLoopback(HttpListener listener, int port)
    {
        _listener = listener;
        Port = port;
    }

    public int Port { get; }

    public Uri RedirectUri => RedirectUriAt(Port);

    public static Uri RedirectUriAt(int port) => new($"http://127.0.0.1:{port}/callback");

    // Null when no loopback port could be opened.
    public static McpOAuthLoopback? Open(ILogger logger)
    {
        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            listener.Start();
            return new McpOAuthLoopback(listener, port);
        }
        catch (HttpListenerException exception)
        {
            logger.LogWarning(exception, "Could not open the loopback listener on port {Port} for an MCP OAuth redirect", port);
            listener.Close();
            return null;
        }
    }

    // The redirect's query for `state`, or null when the wait was cancelled. Never logged: it carries the code.
    public async Task<string?> ReceiveAsync(string serverName, string state, CancellationToken cancellationToken, string kind = "MCP server")
    {
        using var registration = cancellationToken.Register(_listener.Stop);
        try
        {
            while (true)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                var query = context.Request.Url?.Query ?? "";
                var answer = ParseCallback(query);
                if (context.Request.Url?.AbsolutePath != "/callback" || !string.Equals(answer.State, state, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                    context.Response.Close();
                    continue;
                }

                // The redirect counts once it arrived; a tab closed before the page reached it does not lose the code.
                try
                {
                    await RespondAsync(context, serverName, answer.Error is null, kind).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpListenerException or IOException or ObjectDisposedException)
                {
                }

                return query.TrimStart('?');
            }
        }
        catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _listener.Close();

    // The `state` an authorization link carries, which its redirect has to bring back.
    public static string? StateOf(Uri authorizationUri) => ParseCallback(authorizationUri.Query).State;

    public static (string? Code, string? State, string? Iss, string? Error) ParseCallback(string? query)
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

    // AC-1517: names what was signed in to (`kind`: an MCP server, or a plugin's account at a provider, AC-1407) and that
    // this tab is Cockpit's own. Only the first redirect gets this page: the listener stops after it, so a reload or a
    // late attempt reaches a closed port and the browser shows its own "can't connect" page.
    public static async Task RespondAsync(HttpListenerContext context, string serverName, bool success, string kind = "MCP server")
    {
        var server = WebUtility.HtmlEncode(serverName);
        var what = WebUtility.HtmlEncode(kind);
        var message = success
            ? $"This tab was Cockpit's sign-in for the {what} \"{server}\", and it has been handed back to Cockpit. You can close this tab."
            : $"Cockpit's sign-in for the {what} \"{server}\" failed or was cancelled. You can close this tab; "
                + $"to try again, press Sign in for that {what} in Cockpit.";
        var body = Encoding.UTF8.GetBytes(
            $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Cockpit</title></head><body style=\"font-family:sans-serif\">{message}</body></html>");

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    // Answers whether the URL was handed over at all — not whether a window appeared, which this process cannot see.
    // A scheme this refuses to give the shell, or a desktop with nothing on PATH to open one, throws or returns
    // early; a handler that then declines the URL does so out of reach, and is why the stage says "requested".
    public static bool OpenBrowser(Uri url, ILogger logger)
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
    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
