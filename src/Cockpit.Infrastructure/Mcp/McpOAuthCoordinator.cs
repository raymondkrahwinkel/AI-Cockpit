using System.Net.Sockets;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

// AC-353: decides what credential a session may present to an OAuth-protected server and renews a stale one.
// `toastNotifier` (AC-524) is optional since the screenshot harness and tests have no desktop to show on.
internal sealed class McpOAuthCoordinator(
    IMcpOAuthTokenStore tokenStore,
    IMcpOAuthAuthorizer authorizer,
    ILogger<McpOAuthCoordinator> logger,
    IToastNotifier? toastNotifier = null) : IMcpOAuthCoordinator, ISingletonService
{
    // How much of an access token's remaining life has to be left for it to be worth using for *one request*:
    // enough to survive the round trip it is about to be spent on and no more.
    private static readonly TimeSpan RequestExpiryMargin = TimeSpan.FromMinutes(2);

    // AC-771: when a renewal *starts*, deliberately earlier than the margin above, which is only what one call needs
    // to survive its round trip. The gap between the two is a grace period — calls keep being served from the token
    // in hand while the renewal is retried — so a token endpoint that is briefly down costs nothing.
    private static readonly TimeSpan RequestRenewalLead = TimeSpan.FromMinutes(10);

    // AC-524: session margin held for 55 minutes rather than the CLI's one-hour token, so a session always starts
    // on a just-renewed token — fixed since the store keeps only the expiry. A server whose tokens never live
    // this long reports McpOAuthAttentionReason.TokenTooShortLived rather than a token that dies mid-session.
    private static readonly TimeSpan SessionExpiryMargin = TimeSpan.FromMinutes(55);

    // AC-524: last unusable-reason per server, so the proxy (which runs this on every request) reports a
    // transition once instead of logging the same line per request.
    private readonly Dictionary<string, McpOAuthAttentionReason> _reported = new(StringComparer.Ordinal);
    private readonly Lock _reportedLock = new();

    // AC-403: renewal in flight per server id — everyone who arrives while it runs waits for that one. Not
    // tidiness: these servers rotate refresh tokens, so a concurrent second renewal replays a redeemed token and
    // can get the whole authorization revoked.
    private readonly Dictionary<string, Task<HandshakeOutcome>> _renewals = new(StringComparer.Ordinal);
    private readonly Lock _renewalsLock = new();

    // How much life a fresh token from this server was last measured to have, by stable id. A lead longer than that
    // is a lead the server's own tokens can never clear, and renewing on it starts a renewal per call that changes
    // nothing — a replayed refresh grant on anything that rotates, which is the outage `_renewals` exists to stop.
    private readonly Dictionary<string, TimeSpan> _freshTokenLife = new(StringComparer.Ordinal);
    private readonly Lock _freshTokenLifeLock = new();

    public async Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        // Interactive sign-in must actually run — answering from a stored token would make "Sign in again" a
        // button that does nothing when the server has stopped honouring a token that still looks fine here.
        if (interactive)
        {
            // Clear first since the SDK reads the stored token through the cache; put the old one back if the
            // flow produced nothing, so closing the browser window doesn't cost a working credential.
            var previous = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
            await SignOutAsync(server, cancellationToken).ConfigureAwait(false);

            // replacing: null — SignOutAsync above emptied the store, so anything found afterwards came out of this
            // sign-in and nothing has to be compared against a leftover.
            var signedIn = await _ConnectAndReadAsync(server, interactive: true, OneCallWithoutFallingBack, replacing: null, cancellationToken).ConfigureAwait(false);

            // Restore only when the flow left nothing behind, not merely on "not authorized" — a short-lived
            // token still counts as that verdict, and restoring over it would discard the fresh sign-in.
            if (previous is not null && await _ReadAsync(server, cancellationToken).ConfigureAwait(false) is null)
            {
                await tokenStore.SaveAsync(server.IdentityKey, server.Name, previous, cancellationToken).ConfigureAwait(false);
            }

            return signedIn;
        }

        // The credential is spent on one call and asked for again on the next, so a renewal that did not happen is
        // not a reason to refuse a token that still covers this call — it is a reason to try again next time.
        return await _RenewIfNeededAsync(server, OneCall, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        // No such grace here, and that asymmetry is the point: this answer is written into a config the session
        // reads once and holds for hours. Serving a token that is merely good *now* is precisely the session that
        // loses its server an hour in — the defect AC-524 exists for.
        return await _RenewIfNeededAsync(server, AWholeSession, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        var stored = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
        if (stored is null || !stored.IsForResource(server.Url))
        {
            return _Unauthorized(server, McpOAuthAttentionReason.NeverSignedIn, McpSignInStage.NoBrowserLaunched);
        }

        // Somebody already replaced it. This is the ordinary shape of a hundred calls in flight against a credential
        // the server has just started refusing: the first renews, and every other one arrives here to find a token it
        // has not tried yet. Handing that one back is what keeps the burst to a single round trip.
        if (!string.Equals(stored.AccessToken, rejectedAccessToken, StringComparison.Ordinal))
        {
            return _Authorized(server, stored.AccessToken);
        }

        if (string.IsNullOrWhiteSpace(stored.RefreshToken))
        {
            return _Unauthorized(server, McpOAuthAttentionReason.SignInExpired, McpSignInStage.NoBrowserLaunched);
        }

        // Said out loud because it contradicts what this process believed a moment ago, and that disagreement is the
        // only evidence there will ever be of a grant revoked at the far end or a rotation race lost to another
        // session. Never the token itself (Iron Law #8) — the expiry it claimed is what makes the point.
        logger.LogWarning(
            "MCP server {Server} refused an access token the cockpit still considered valid until {ExpiresAt}; renewing it rather than presenting it again.",
            server.Name,
            stored.ExpiresAt);

        // No falling back to what is held, however much life its clock claims: the server just refused that exact
        // token, so handing it back is a round trip spent on an answer already known.
        return await _ConnectAndReadAsync(server, interactive: false, OneCallWithoutFallingBack, stored.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    // The ladder every automatic credential use walks: a token with room to spare is used silently; a short one
    // renews itself against the refresh grant; the operator is asked only when renewal cannot happen.
    //
    private async Task<McpOAuthAccess> _RenewIfNeededAsync(McpServerConfig server, RenewalPolicy policy, CancellationToken cancellationToken)
    {
        // AC-403: a token stored under the server's stable id can still be for the wrong address (a project entry
        // can repoint the same id, and the id survives a URL edit) — treated as absent rather than renewed there.
        var stored = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
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

        var renewAt = _RenewAtFor(server, policy);
        if (stored is not null && stored.IsUsableAt(DateTimeOffset.UtcNow, renewAt))
        {
            return _Authorized(server, stored.AccessToken);
        }

        // Nothing to renew from and nobody to ask: say so rather than spend a round trip on a handshake that cannot
        // succeed. This is the ordinary "never signed in" case, and it has to be cheap — it runs on every start.
        if (string.IsNullOrWhiteSpace(stored?.RefreshToken))
        {
            return _Unauthorized(
                server,
                stored is null ? McpOAuthAttentionReason.NeverSignedIn : McpOAuthAttentionReason.SignInExpired,
                McpSignInStage.NoBrowserLaunched);
        }

        // Renewal is a measurable event, not folklore: without this line the only trace of an expiry was a session
        // that quietly had fewer tools (AC-524, criterion 4). Never the token itself, in any form (Iron Law #8).
        logger.LogInformation(
            "The access token for MCP server {Server} expires at {ExpiresAt} and is inside the {Margin} margin this use keeps, so it is being renewed.",
            server.Name,
            stored.ExpiresAt,
            renewAt);

        return await _ConnectAndReadAsync(server, interactive: false, policy, stored.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    // Connects once and reports what that left in the store, letting the SDK drive the refresh/authorization flow
    // rather than a second hand-rolled OAuth implementation. `replacing` is the prior access token (or null),
    // which tells "produced something unusable" apart from "produced nothing".
    private async Task<McpOAuthAccess> _ConnectAndReadAsync(
        McpServerConfig server,
        bool interactive,
        RenewalPolicy policy,
        string? replacing,
        CancellationToken cancellationToken)
    {
        var renewAt = _RenewAtFor(server, policy);

        // An interactive sign-in stays outside the single-flight gate: the operator pressed the button, this method
        // already cleared the stored token so the flow would actually run, and joining somebody else's silent
        // renewal would make that button do nothing again.
        var (stage, explained, unreachable, grantRejected) = interactive
            ? await _HandshakeAsync(server, interactive: true, renewAt, cancellationToken).ConfigureAwait(false)
            : await _SharedRenewalAsync(server, renewAt, cancellationToken).ConfigureAwait(false);

        // Read after the renewal, never from before it: a caller that queued behind someone else's renewal almost
        // always finds a fresh token here, and the whole point of waiting was to use it rather than to redeem the
        // same refresh token a second time.
        var token = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
        var renewed = token is not null
            && token.IsForResource(server.Url)
            && !string.Equals(token.AccessToken, replacing, StringComparison.Ordinal);

        if (renewed)
        {
            _RememberWhatItsFreshTokensAreWorth(server, token!);
        }

        if (renewed && token!.IsUsableAt(DateTimeOffset.UtcNow, policy.UsableFrom))
        {
            logger.LogInformation(
                "Renewed the authorization for MCP server {Server}; the new access token runs to {ExpiresAt}.",
                server.Name,
                token.ExpiresAt);

            return _Authorized(server, token.AccessToken) with { SignInStage = stage };
        }

        // AC-771: the renewal produced nothing, but what is held still covers this call — so it is used, and every
        // call until the expiry tries the renewal again. Only where a credential is spent per call: a session holds
        // its answer for hours, and a token the server has just refused is one this end's clock is wrong about.
        if (!renewed && policy.ServeTheHeldTokenInstead && token is not null && token.IsForResource(server.Url)
            && token.IsUsableAt(DateTimeOffset.UtcNow, policy.UsableFrom))
        {
            logger.LogInformation(
                "Renewing the authorization for MCP server {Server} did not succeed, but the access token in hand runs to {ExpiresAt} and still covers this call, so it is used and the renewal is attempted again on the next one.",
                server.Name,
                token.ExpiresAt);

            return _Authorized(server, token.AccessToken) with { SignInStage = stage };
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

        var reason = _ReasonFor(renewed, unreachable, grantRejected, interactive);

        if (renewed)
        {
            logger.LogWarning(
                "MCP server {Server} renewed to an access token that expires at {ExpiresAt}, sooner than the {Margin} this use has to hold it for.",
                server.Name,
                token!.ExpiresAt,
                policy.UsableFrom);
        }

        // An interactive failure is already in front of the operator — they pressed the button and the dialog is
        // waiting on it — so it is not reported a second time through the channel that exists for the automatic path.
        return interactive
            ? McpOAuthAccess.AuthorizationRequired with { SignInStage = stage, Reason = reason }
            : _Unauthorized(server, reason, stage);
    }

    // AC-646: why a handshake produced no usable credential, told apart since the advice differs — a short-lived
    // renewal is not an expired sign-in, and "expired" is only said when the server actually said invalid_grant.
    // Interactive sign-in keeps its own reading, since the operator is standing in front of the failed flow.
    private static McpOAuthAttentionReason _ReasonFor(bool renewed, bool unreachable, bool grantRejected, bool interactive) =>
        renewed ? McpOAuthAttentionReason.TokenTooShortLived
        : unreachable ? McpOAuthAttentionReason.ServerUnreachable
        : grantRejected || interactive ? McpOAuthAttentionReason.SignInExpired
        : McpOAuthAttentionReason.RenewalCouldNotBeConfirmed;

    // One silent renewal per server at a time (`_renewals`); callers that arrive while one runs wait for it and
    // re-read the store. Keying by margin instead would let two renewals race, which is what `_renewals` prevents.
    private Task<HandshakeOutcome> _SharedRenewalAsync(McpServerConfig server, TimeSpan margin, CancellationToken cancellationToken)
    {
        Task<HandshakeOutcome> renewal;
        lock (_renewalsLock)
        {
            if (!_renewals.TryGetValue(server.IdentityKey, out var running))
            {
                running = _RenewAndClearAsync(server, margin);
                _renewals[server.IdentityKey] = running;
            }

            renewal = running;
        }

        // Each caller's own deadline applies to its wait and not to the shared work: the first arrival's budget must
        // not decide the fate of everyone who queued behind it.
        return renewal.WaitAsync(cancellationToken);
    }

    // Runs the renewal off-thread and removes it from `_renewals` as the last thing the same task does, not a
    // detached continuation — that left a gap where a caller found the slot empty or stale. Measured in CI
    // (`McpOAuthSessionMarginTests`), not theoretical.
    private Task<HandshakeOutcome> _RenewAndClearAsync(McpServerConfig server, TimeSpan margin) => Task.Run(async () =>
    {
        try
        {
            return await _RenewAsync(server, margin).ConfigureAwait(false);
        }
        finally
        {
            lock (_renewalsLock)
            {
                _renewals.Remove(server.IdentityKey);
            }
        }
    });

    // The shared renewal itself. It never faults, by construction: this task is handed to every waiting caller, and
    // a faulted one would be re-thrown into each of them — including onto paths that only ever read the store to
    // find out what happened. Whatever went wrong is folded into the outcome instead.
    private async Task<HandshakeOutcome> _RenewAsync(McpServerConfig server, TimeSpan margin)
    {
        try
        {
            // CancellationToken.None on purpose: this work is shared, so honouring the first caller's token would
            // let whoever happened to arrive first cancel a renewal the others are still waiting on.
            return await _HandshakeAsync(server, interactive: false, margin, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Renewing the authorization for MCP server {Server} ended unexpectedly.", server.Name);
            return new HandshakeOutcome(McpSignInStage.NoBrowserLaunched, Explained: false, Unreachable: _IsUnreachable(exception), GrantRejected: false);
        }
    }

    // A credential works, so whatever was last reported about this server is no longer true: forgetting it is what
    // lets the next failure be announced instead of swallowed as "already said that".
    private McpOAuthAccess _Authorized(McpServerConfig server, string accessToken)
    {
        lock (_reportedLock)
        {
            _reported.Remove(server.IdentityKey);
        }

        return McpOAuthAccess.Authorized(accessToken);
    }

    // Says it once — written on the way into a state, not on every repeat of the same failure — under one lock so
    // two requests hitting a newly-stale server can't both find nothing recorded and both announce it.
    private McpOAuthAccess _Unauthorized(McpServerConfig server, McpOAuthAttentionReason reason, McpSignInStage stage)
    {
        bool announce;
        lock (_reportedLock)
        {
            announce = !_reported.TryGetValue(server.IdentityKey, out var previous) || previous != reason;
            _reported[server.IdentityKey] = reason;
        }

        if (announce)
        {
            var guidance = McpOAuthSignInGuidance.For(server.Name, reason);
            logger.LogWarning("MCP server {Server} is unavailable: {Guidance}", server.Name, guidance);

            // AC-646: the log line is written either way; the toast is not — an unconfirmed renewal retries
            // itself, so interrupting the operator over a one-second storm would be the wrong fix.
            if (reason != McpOAuthAttentionReason.RenewalCouldNotBeConfirmed)
            {
                _NotifyOperator(server, guidance);
            }
        }

        return McpOAuthAccess.AuthorizationRequired with { SignInStage = stage, Reason = reason };
    }

    // Puts the same sentence where the operator will meet it, not just in a log they'd have to know to check.
    // Fire-and-forget with the failure caught inside, since a toast that can't be shown must not take down the
    // credential path reporting it.
    private void _NotifyOperator(McpServerConfig server, string guidance) => _ = Task.Run(async () =>
    {
        if (toastNotifier is null)
        {
            return;
        }

        try
        {
            await toastNotifier.NotifyAsync(new AttentionNotification("MCP server unavailable", guidance)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not show the operator a notification about MCP server {Server}.", server.Name);
        }
    });

    public async Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpAuthState.NotRequired;
        }

        var stored = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
        if (stored is null || !stored.IsForResource(server.Url))
        {
            return McpAuthState.AuthorizationRequired;
        }

        // A token that can still be renewed counts as signed in whatever its clock says, since the next use
        // renews it silently; without a refresh token the same margin as the credential path applies.
        return !string.IsNullOrWhiteSpace(stored.RefreshToken) || stored.IsUsableAt(DateTimeOffset.UtcNow, RequestExpiryMargin)
            ? McpAuthState.Authorized
            : McpAuthState.AuthorizationRequired;
    }

    public Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
        tokenStore.RemoveAsync(server.IdentityKey, cancellationToken);

    // Keyed by the server's stable id (AC-403), reported by its name: the id is what finds the token across a
    // rename, and the name is the only one of the two an operator reading the log would recognise.
    private async Task<McpOAuthToken?> _ReadAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        try
        {
            return await tokenStore.GetAsync(server.IdentityKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A config read that fails leaves the server unauthorized rather than failing the session start, which is
            // how every other read of this file behaves. Never log the token itself (Iron Law #8) — only the server.
            logger.LogWarning(exception, "Reading the stored MCP credential for {Server} failed; treating it as not signed in.", server.Name);
            return null;
        }
    }

    // AC-457: connects far enough for the SDK to finish the outstanding OAuth step, then drops the client and
    // reports how far it got, via its own minimal transport (the tool provider's would cycle the graph). AC-524:
    // `Unreachable` separates "server said no" from "server said nothing" — only the first is fixable by sign-in.
    private async Task<HandshakeOutcome> _HandshakeAsync(McpServerConfig server, bool interactive, TimeSpan margin, CancellationToken cancellationToken)
    {
        if (server.Transport != McpTransport.Http || string.IsNullOrWhiteSpace(server.Url))
        {
            // No address to reach is a configuration gap, not an outage — an operator who is told to wait for the
            // server to come back would wait forever.
            return new HandshakeOutcome(McpSignInStage.NoBrowserLaunched, Explained: false, Unreachable: false, GrantRejected: false);
        }

        var stageRecorder = new McpSignInStageRecorder();

        // The transport's own HttpClient, made rather than left to the SDK, so the token endpoint's answer passes
        // through something that can see it (AC-646). Owned by the transport and disposed with it — the SDK builds
        // one per transport anyway, so this costs the same connection pool it already had.
        var grantWatcher = new McpOAuthGrantRejectionWatcher { InnerHandler = new HttpClientHandler() };
        try
        {
            await using var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = server.Name,
                    Endpoint = new Uri(server.Url),
                    TransportMode = HttpTransportMode.AutoDetect,
                    OAuth = authorizer.CreateOptions(server, interactive, stageRecorder, margin),
                },
                new HttpClient(grantWatcher),
                loggerFactory: null,
                ownsHttpClient: true);

            var clientOptions = interactive ? McpInteractiveOAuthClientOptions.Create() : null;
            await using var client = await McpClientConnector.ConnectAsync(transport, clientOptions, cancellationToken).ConfigureAwait(false);
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

            return new HandshakeOutcome(stageRecorder.Reached, Explained: true, Unreachable: _IsUnreachable(exception), grantWatcher.GrantRejected);
        }
        catch (Exception exception)
        {
            // An expected outcome, not an anomaly: with nobody to ask, this is exactly what a refusal to hand a
            // sign-in to a browser looks like. The caller decides what it means by re-reading the store.
            logger.LogInformation(exception, "Could not renew authorization for MCP server {Server} without asking the operator.", server.Name);
            return new HandshakeOutcome(stageRecorder.Reached, Explained: false, Unreachable: _IsUnreachable(exception), grantWatcher.GrantRejected);
        }

        return new HandshakeOutcome(stageRecorder.Reached, Explained: false, Unreachable: false, grantWatcher.GrantRejected);
    }

    // Whether the handshake failed because nothing answered, rather than because what answered refused. Walked down
    // the inner exceptions because the MCP client wraps a transport failure in its own type, so the socket error that
    // says "down" is never the one that surfaces.
    private static bool _IsUnreachable(Exception exception) =>
        exception is HttpRequestException or SocketException or TimeoutException
        || (exception.InnerException is { } inner && _IsUnreachable(inner));

    // What one use of a credential asks of it. `RenewAt`: how much life left starts a renewal. `UsableFrom`: how
    // much a token needs to be handed over at all, never wider — the gap between them is the grace period.
    // `ServeTheHeldTokenInstead`: whether a renewal that produced nothing may be answered with the token in hand.
    private readonly record struct RenewalPolicy(TimeSpan RenewAt, TimeSpan UsableFrom, bool ServeTheHeldTokenInstead);

    private static readonly RenewalPolicy OneCall = new(RequestRenewalLead, RequestExpiryMargin, ServeTheHeldTokenInstead: true);

    private static readonly RenewalPolicy OneCallWithoutFallingBack = new(RequestRenewalLead, RequestExpiryMargin, ServeTheHeldTokenInstead: false);

    private static readonly RenewalPolicy AWholeSession = new(SessionExpiryMargin, SessionExpiryMargin, ServeTheHeldTokenInstead: false);

    // The lead this server's own tokens can actually clear. One that issues tokens shorter than the lead would
    // otherwise be renewed on every call — each renewal succeeds, the fresh token still falls inside the lead, and
    // the next call starts another — so for that server the margin a single call needs is the lead (AC-771).
    private TimeSpan _RenewAtFor(McpServerConfig server, RenewalPolicy policy)
    {
        lock (_freshTokenLifeLock)
        {
            return _freshTokenLife.TryGetValue(server.IdentityKey, out var life) && policy.RenewAt >= life
                ? policy.UsableFrom
                : policy.RenewAt;
        }
    }

    // Measured rather than configured, and re-measured on every renewal: a server that starts issuing longer tokens
    // gets its grace period back without anyone being told, and one that shortens them loses it the same way.
    private void _RememberWhatItsFreshTokensAreWorth(McpServerConfig server, McpOAuthToken fresh)
    {
        if (fresh.ExpiresAt is not { } expiresAt)
        {
            return;
        }

        lock (_freshTokenLifeLock)
        {
            _freshTokenLife[server.IdentityKey] = expiresAt - DateTimeOffset.UtcNow;
        }
    }

    // AC-457: `Stage` says where a sign-in stopped, `Explained` whether the handshake already logged it.
    // AC-524: `Unreachable` is "nothing answered" vs. a refusal. AC-646: `GrantRejected` is the only real
    // evidence a sign-in is gone — without it "expired" would be a guess.
    private readonly record struct HandshakeOutcome(McpSignInStage Stage, bool Explained, bool Unreachable, bool GrantRejected);
}
