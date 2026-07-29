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

    // AC-505 follow-up (2026-07-29): see McpToolProvider.InteractiveOAuthClientOptions — the same SDK 2.0.0
    // DiscoverProbeTimeout/InitializationTimeout pairing has to widen here too, only for the operator-facing
    // "sign in"/"sign in again" path; a non-interactive renewal check should still fail fast.
    private static readonly McpClientOptions InteractiveOAuthClientOptions = new()
    {
        InitializationTimeout = TimeSpan.FromMinutes(5),
        DiscoverProbeTimeout = TimeSpan.FromMinutes(5),
    };

    public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        // Asking interactively is the operator saying "sign me in", which has to mean a sign-in actually happens.
        // Answering from a stored token would make "Sign in again" a button that does nothing — and the case it
        // exists for is precisely the one where the stored token looks fine here but the server has stopped
        // honouring it. Clearing first is what makes the flow run rather than the cache answer.
        if (interactive)
        {
            // Clearing first is mechanically necessary: the SDK reads the stored token through the cache, so leaving
            // it in place means the flow never runs and the button does nothing. But losing a working credential
            // because a browser window was closed is not a price for pressing "sign in again" — so the old one is
            // put back when the flow produced nothing.
            var previous = await _ReadAsync(server.Name, cancellationToken).ConfigureAwait(false);
            await SignOutAsync(server, cancellationToken).ConfigureAwait(false);

            var signedIn = await _ConnectAndReadAsync(server, interactive: true, cancellationToken).ConfigureAwait(false);

            // Put the old one back only when the flow left nothing behind — not merely when the answer was "not
            // authorized". A sign-in that succeeds and issues a short-lived token gets that verdict too (the answer
            // keeps a margin), and restoring over it would throw away the credential the operator just went to the
            // browser for and hand back the stale one.
            if (previous is not null && await _ReadAsync(server.Name, cancellationToken).ConfigureAwait(false) is null)
            {
                await tokenStore.SaveAsync(server.Name, previous, cancellationToken).ConfigureAwait(false);
            }

            return signedIn;
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
        if (string.IsNullOrWhiteSpace(stored?.RefreshToken))
        {
            return McpOAuthAccess.AuthorizationRequired;
        }

        return await _ConnectAndReadAsync(server, interactive: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects once and reports what that left in the store. The SDK owns both the refresh grant and the full
    /// authorization flow, and both run inside a connect, writing any new token through the cache the authorizer
    /// installs — so connecting and then reading is how either is driven, rather than a second, hand-rolled OAuth
    /// implementation drifting alongside the SDK's.
    /// </summary>
    private async Task<McpOAuthAccess> _ConnectAndReadAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken)
    {
        var (stage, explained) = await _HandshakeAsync(server, interactive, cancellationToken).ConfigureAwait(false);

        var token = await _ReadAsync(server.Name, cancellationToken).ConfigureAwait(false);
        if (token is not null && token.IsForResource(server.Url) && token.IsUsableAt(DateTimeOffset.UtcNow, ExpiryMargin))
        {
            return McpOAuthAccess.Authorized(token.AccessToken) with { SignInStage = stage };
        }

        // The dialog tells the operator the reason is in the log, so an interactive failure has to leave one there
        // whatever happened — and a handshake can end without an exception: a server with no address to reach never
        // runs one, and a connect that is never challenged throws nothing and still yields no credential.
        if (interactive && !explained)
        {
            logger.LogWarning(
                "The sign-in for MCP server {Server} produced no usable credential; it got as far as {SignInStage}.",
                server.Name,
                stage);
        }

        return McpOAuthAccess.AuthorizationRequired with { SignInStage = stage };
    }

    public async Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpAuthState.NotRequired;
        }

        var stored = await _ReadAsync(server.Name, cancellationToken).ConfigureAwait(false);
        if (stored is null || !stored.IsForResource(server.Url))
        {
            return McpAuthState.AuthorizationRequired;
        }

        // A token that can still be renewed counts as signed in whatever its clock says: the next use renews it
        // without the operator noticing, so asking them to sign in again would be asking for nothing. Without a
        // refresh token the same margin applies as on the credential path — a token with a minute left is one a
        // session start will already refuse, and a status that says "signed in" about it is wrong about exactly the
        // case this exists to make visible.
        return !string.IsNullOrWhiteSpace(stored.RefreshToken) || stored.IsUsableAt(DateTimeOffset.UtcNow, ExpiryMargin)
            ? McpAuthState.Authorized
            : McpAuthState.AuthorizationRequired;
    }

    public Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
        tokenStore.RemoveAsync(server.Name, cancellationToken);

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

    // Connects far enough to make the SDK complete whatever OAuth step is outstanding, then drops the client again,
    // and answers how far a sign-in got (AC-457) so the caller can say where it stopped without being handed the
    // reason it stopped. Deliberately its own minimal transport rather than the tool provider's: that one overlays
    // built-in servers and session tokens, and depending on it from here would put a cycle in the graph the tool
    // provider already sits in.
    // <c>Explained</c> says whether this already wrote the operator's line, so the caller can supply one when it did
    // not rather than write a second next to it.
    private async Task<(McpSignInStage Stage, bool Explained)> _HandshakeAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken)
    {
        if (server.Transport != McpTransport.Http || string.IsNullOrWhiteSpace(server.Url))
        {
            return (McpSignInStage.NoBrowserLaunched, false);
        }

        var stageRecorder = new McpSignInStageRecorder();
        try
        {
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                TransportMode = HttpTransportMode.AutoDetect,
                OAuth = authorizer.CreateOptions(server, interactive, stageRecorder),
            });

            var clientOptions = interactive ? InteractiveOAuthClientOptions : null;
            await using var client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller put a bound on how long it would wait. Swallowing that would leave it unable to tell a
            // server that refused from one that simply took too long, which are different things to report.
            throw;
        }
        catch (Exception exception) when (interactive)
        {
            // The operator pressed Sign in and is waiting on this, so it is a failure someone is standing in front
            // of rather than the routine outcome below — and the sentence below would say the opposite of what
            // happened, since asking is precisely what they did.
            logger.LogWarning(
                exception,
                "The sign-in for MCP server {Server} did not complete; it got as far as {SignInStage}.",
                server.Name,
                stageRecorder.Reached);

            return (stageRecorder.Reached, true);
        }
        catch (Exception exception)
        {
            // An expected outcome, not an anomaly: with nobody to ask, this is exactly what a refusal to hand a
            // sign-in to a browser looks like. The caller decides what it means by re-reading the store.
            logger.LogInformation(exception, "Could not renew authorization for MCP server {Server} without asking the operator.", server.Name);
        }

        return (stageRecorder.Reached, false);
    }
}
