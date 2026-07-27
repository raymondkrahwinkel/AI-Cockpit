using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Decides what credential a session may present to an OAuth-protected MCP server, and renews one that has gone
/// stale (AC-353).
/// </summary>
internal sealed class McpOAuthCoordinator(
    IMcpOAuthTokenStore tokenStore,
    IMcpOAuthAuthorizer authorizer,
    ILogger<McpOAuthCoordinator> logger) : IMcpOAuthCoordinator, ISingletonService
{
    /// <summary>
    /// How much of an access token's remaining life has to be left for it to be worth handing over. A config file is
    /// written once at session start and read for as long as the session lasts, so a token with seconds on the clock
    /// is a session that breaks a minute in.
    /// </summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        // A token is stored under the server's name, and a name is not an identity — a project's own entry replaces a
        // registry server by name and may carry a different address, and a rename does the same. So a token that was
        // not issued for this address is treated as absent, refresh token and all: renewing with the other host's
        // grant would be the same mistake one step later.
        var stored = await _ReadAsync(server.Name, cancellationToken).ConfigureAwait(false);
        if (stored is not null && !stored.IsForResource(server.Url))
        {
            logger.LogWarning(
                "The stored credential for MCP server {Server} does not belong to the address it now points at, so it will not be used; signing in again will replace it.",
                server.Name);

            // Discarded rather than returned on: treated as absent is what the rule above says, and absent is a state
            // an interactive sign-in can still recover from. Returning here instead would leave a name that changed
            // hands permanently unauthorizable, since the stale record is never removed either.
            stored = null;
        }

        if (stored is not null && stored.IsUsableAt(DateTimeOffset.UtcNow, ExpiryMargin))
        {
            return McpOAuthAccess.Authorized(stored.AccessToken);
        }

        // Nothing to renew from and nobody to ask: say so rather than spend a round trip on a handshake that cannot
        // succeed. This is the ordinary "never signed in" case, and it has to be cheap — it runs on every start.
        if (!interactive && string.IsNullOrWhiteSpace(stored?.RefreshToken))
        {
            return McpOAuthAccess.AuthorizationRequired;
        }

        // The SDK owns the refresh grant and the full authorization flow; both run inside a connect, writing any new
        // token through the cache the authorizer installs. So the way to renew is to connect once and then read what
        // the cache holds — rather than a second, hand-rolled OAuth implementation drifting alongside the SDK's.
        await _HandshakeAsync(server, interactive, cancellationToken).ConfigureAwait(false);

        var renewed = await _ReadAsync(server.Name, cancellationToken).ConfigureAwait(false);
        return renewed is not null && renewed.IsForResource(server.Url) && renewed.IsUsableAt(DateTimeOffset.UtcNow, ExpiryMargin)
            ? McpOAuthAccess.Authorized(renewed.AccessToken)
            : McpOAuthAccess.AuthorizationRequired;
    }

    private async Task<McpOAuthToken?> _ReadAsync(string serverName, CancellationToken cancellationToken)
    {
        try
        {
            return await tokenStore.GetAsync(serverName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A config read that fails leaves the server unauthorized rather than failing the session start, which is
            // how every other read of this file behaves. Never log the token itself (Iron Law #8) — only the server.
            logger.LogWarning(exception, "Reading the stored MCP credential for {Server} failed; treating it as not signed in.", serverName);
            return null;
        }
    }

    // Connects far enough to make the SDK complete whatever OAuth step is outstanding, then drops the client again.
    // Deliberately its own minimal transport rather than the tool provider's: that one overlays built-in servers and
    // session tokens, and depending on it from here would put a cycle in the graph the tool provider already sits in.
    private async Task _HandshakeAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken)
    {
        if (server.Transport != McpTransport.Http || string.IsNullOrWhiteSpace(server.Url))
        {
            return;
        }

        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                TransportMode = HttpTransportMode.AutoDetect,
                OAuth = authorizer.CreateOptions(server, interactive),
            });

            await using var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller put a bound on how long it would wait. Swallowing that would leave it unable to tell a
            // server that refused from one that simply took too long, which are different things to report.
            throw;
        }
        catch (Exception exception)
        {
            // An expected outcome, not an anomaly: non-interactively this is exactly what a refusal to open a browser
            // looks like. The caller decides what it means by re-reading the store.
            logger.LogInformation(exception, "Could not renew authorization for MCP server {Server} without asking the operator.", server.Name);
        }
    }
}
