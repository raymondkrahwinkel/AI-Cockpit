using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Mcp;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Services;

// Says once, at startup, which MCP servers are waiting to be signed in to, and offers to do it there and then.
// A backup carries no credentials by design, so a restored cockpit keeps its Depot connection and loses the
// sign-in: `GetStateAsync` knew, but only Options and the New-session dialog ever asked. An expired token too.
internal sealed class McpSignInNotice(
    IMcpServerCatalog servers,
    IMcpOAuthCoordinator oauth,
    IToastService toasts,
    ILogger<McpSignInNotice> logger) : ISingletonService
{
    // Silent when everything is signed in — this runs on every start, and a notice that appears when nothing is
    // wrong is one the operator learns to dismiss without reading.
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var waiting = new List<McpServerConfig>();
            foreach (var server in await servers.GetServersAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await oauth.GetStateAsync(server, cancellationToken).ConfigureAwait(false) == McpAuthState.AuthorizationRequired)
                {
                    waiting.Add(server);
                }
            }

            if (waiting.Count == 0)
            {
                return;
            }

            logger.LogInformation(
                "Not signed in to {Count} MCP server(s): {Servers}", waiting.Count, string.Join(", ", waiting.Select(server => server.Name)));

            toasts.Show(_Message(waiting), ToastSeverity.Warning, "Sign in", () => _ = _SignInAsync(waiting, cancellationToken));
        }
        catch (Exception exception)
        {
            // Working out whether to say something is not worth holding up a cockpit that is otherwise fine.
            logger.LogWarning(exception, "Could not check which MCP servers still need signing in to");
        }
    }

    // One notice for all of them, named: the operator's question is which connections are dead, and one toast per
    // server would bury that under itself.
    private static string _Message(IReadOnlyList<McpServerConfig> waiting)
    {
        var names = string.Join(", ", waiting.Select(server => server.Name));

        return waiting.Count == 1
            ? $"Not signed in to {names}. Everything it provides stays empty until you are."
            : $"Not signed in to {waiting.Count} servers: {names}. Everything they provide stays empty until you are.";
    }

    // One at a time: each interactive acquire opens a browser, and a handful at once is a stack of windows over
    // an operator who asked for one thing.
    private async Task _SignInAsync(IReadOnlyList<McpServerConfig> waiting, CancellationToken cancellationToken)
    {
        foreach (var server in waiting)
        {
            try
            {
                await oauth.AcquireAsync(server, interactive: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // One server refusing must not cost the sign-in of the ones behind it in the list.
                logger.LogWarning(exception, "Signing in to MCP server {Server} failed", server.Name);
            }
        }
    }
}
