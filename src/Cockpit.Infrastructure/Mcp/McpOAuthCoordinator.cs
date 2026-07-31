using System.Net.Sockets;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Mcp;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Decides what credential a session may present to an OAuth-protected MCP server, and renews one that has gone
/// stale (AC-353).
/// </summary>
/// <param name="toastNotifier">
/// Where a server going unusable is told to the operator (AC-524). Optional because the three routes that build a
/// coordinator without one — the screenshot harness and the tests — have no desktop to show anything on; on those it
/// is the log line alone, which is what they assert against anyway.
/// </param>
internal sealed class McpOAuthCoordinator(
    IMcpOAuthTokenStore tokenStore,
    IMcpOAuthAuthorizer authorizer,
    ILogger<McpOAuthCoordinator> logger,
    IToastNotifier? toastNotifier = null) : IMcpOAuthCoordinator, ISingletonService
{
    /// <summary>
    /// How much of an access token's remaining life has to be left for it to be worth using for <em>one request</em>:
    /// enough to survive the round trip it is about to be spent on and no more.
    /// </summary>
    private static readonly TimeSpan RequestExpiryMargin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The same question asked for a <em>session</em> (AC-524), where the answer is held for hours instead of
    /// milliseconds. Deliberately wider than the one-hour access token the servers in use here issue: at 55 minutes a
    /// stored token is reused only in the first five minutes of its life, so in practice every session starts on a
    /// token that was just renewed — which is the whole point, since the CLI reads its config once and a token that
    /// dies mid-session takes the server's tools with it.
    /// <para>
    /// A fixed margin rather than "renew below half the lifetime" for two reasons. It needs no issue time (the store
    /// keeps only the expiry, so a fraction is not computable without widening the on-disk record), and it already
    /// bounds the churn from both ends: a server issuing long-lived tokens clears 55 minutes and is never renewed at
    /// all, while a server issuing short ones costs exactly one token-endpoint round trip per session start.
    /// </para>
    /// <para>
    /// A server whose tokens never live this long fails the margin on every token it issues, and that is answered
    /// honestly rather than by quietly lowering the bar: it comes back as
    /// <see cref="McpOAuthAttentionReason.TokenTooShortLived"/>, which the session start reads as a server to leave
    /// out. Handing over a token that will be dead in ten minutes would produce exactly the session this ticket
    /// exists to stop — and the case only arises at all when the loopback endpoint, which makes the lifetime
    /// irrelevant, could not be put in front of the server.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SessionExpiryMargin = TimeSpan.FromMinutes(55);

    /// <summary>
    /// The reason each server was last reported unusable for, keyed by its stable id. This is what keeps the
    /// operator from being told the same thing over and over: the proxy (AC-524) runs this path on every single
    /// request, so a line per failure would be a log full of one sentence. Reported on the way into a state and
    /// cleared the moment a credential works again, so the next failure speaks up.
    /// </summary>
    private readonly Dictionary<string, McpOAuthAttentionReason> _reported = new(StringComparer.Ordinal);
    private readonly Lock _reportedLock = new();

    /// <summary>
    /// The renewal currently in flight for a server, keyed by its stable id (AC-403) — everyone who arrives while it
    /// runs waits for that one instead of starting another.
    /// <para>
    /// This is not tidiness. The authorization servers in use here rotate refresh tokens: a successful renewal issues
    /// a new refresh token and marks the old one as redeemed, and a second renewal presenting the same old token is
    /// a replayed grant — which a server is entitled to answer by revoking the whole authorization. Making renewals
    /// more frequent (a wide session margin, and a proxy that touches this path on every request) is exactly what
    /// makes concurrent ones ordinary rather than theoretical: three sessions opening at once is enough. Without this
    /// gate the fix would cause the outage it was built to remove.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, Task<HandshakeOutcome>> _renewals = new(StringComparer.Ordinal);
    private readonly Lock _renewalsLock = new();

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
            var previous = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
            await SignOutAsync(server, cancellationToken).ConfigureAwait(false);

            // replacing: null — SignOutAsync above emptied the store, so anything found afterwards came out of this
            // sign-in and nothing has to be compared against a leftover.
            var signedIn = await _ConnectAndReadAsync(server, interactive: true, RequestExpiryMargin, replacing: null, cancellationToken).ConfigureAwait(false);

            // Put the old one back only when the flow left nothing behind — not merely when the answer was "not
            // authorized". A sign-in that succeeds and issues a short-lived token gets that verdict too (the answer
            // keeps a margin), and restoring over it would throw away the credential the operator just went to the
            // browser for and hand back the stale one.
            if (previous is not null && await _ReadAsync(server, cancellationToken).ConfigureAwait(false) is null)
            {
                await tokenStore.SaveAsync(server.IdentityKey, server.Name, previous, cancellationToken).ConfigureAwait(false);
            }

            return signedIn;
        }

        return await _RenewIfNeededAsync(server, RequestExpiryMargin, cancellationToken).ConfigureAwait(false);
    }

    public async Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        if (server.Auth != McpServerAuth.OAuth)
        {
            return McpOAuthAccess.NotRequired;
        }

        return await _RenewIfNeededAsync(server, SessionExpiryMargin, cancellationToken).ConfigureAwait(false);
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

        // The per-request margin, not the session one: this token is about to be spent on the call that was refused.
        return await _ConnectAndReadAsync(server, interactive: false, RequestExpiryMargin, stored.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The ladder every automatic use of a credential walks, in this order and no other: a token with room to spare
    /// is used and nothing is said; one that is short renews itself silently against the refresh grant, which is the
    /// ordinary case for as long as that grant lives; only when the renewal cannot happen is the operator asked for
    /// anything. Asking while a usable refresh token is sitting there would be a defect, not caution.
    /// </summary>
    /// <param name="margin">How much life a stored token must have left to be used as it stands — the caller's, because a
    /// token spent on one request and a token held for a whole session are not the same question.</param>
    private async Task<McpOAuthAccess> _RenewIfNeededAsync(McpServerConfig server, TimeSpan margin, CancellationToken cancellationToken)
    {
        // A token is stored under the server's stable id (AC-403), but that still does not make it right for the
        // address this server points at now — a project's own entry replaces a registry server by name and may carry
        // a different one, and the id survives an operator editing the URL under it too. So a token that was not
        // issued for this address is treated as absent, refresh token and all: renewing with the other host's grant
        // would be the same mistake one step later.
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

        if (stored is not null && stored.IsUsableAt(DateTimeOffset.UtcNow, margin))
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
            margin);

        return await _ConnectAndReadAsync(server, interactive: false, margin, stored.AccessToken, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Connects once and reports what that left in the store. The SDK owns both the refresh grant and the full
    /// authorization flow, and both run inside a connect, writing any new token through the cache the authorizer
    /// installs — so connecting and then reading is how either is driven, rather than a second, hand-rolled OAuth
    /// implementation drifting alongside the SDK's.
    /// </summary>
    /// <param name="replacing">
    /// The access token that was in the store before this ran, or <see langword="null"/> when there was none. It is
    /// what tells a renewal that produced something unusable apart from one that produced nothing at all — the store
    /// still holds the old record either way, and the two need opposite things said about them.
    /// </param>
    private async Task<McpOAuthAccess> _ConnectAndReadAsync(McpServerConfig server, bool interactive, TimeSpan margin, string? replacing, CancellationToken cancellationToken)
    {
        // An interactive sign-in stays outside the single-flight gate: the operator pressed the button, this method
        // already cleared the stored token so the flow would actually run, and joining somebody else's silent
        // renewal would make that button do nothing again.
        var (stage, explained, unreachable) = interactive
            ? await _HandshakeAsync(server, interactive: true, cancellationToken).ConfigureAwait(false)
            : await _SharedRenewalAsync(server, cancellationToken).ConfigureAwait(false);

        // Read after the renewal, never from before it: a caller that queued behind someone else's renewal almost
        // always finds a fresh token here, and the whole point of waiting was to use it rather than to redeem the
        // same refresh token a second time.
        var token = await _ReadAsync(server, cancellationToken).ConfigureAwait(false);
        var renewed = token is not null
            && token.IsForResource(server.Url)
            && !string.Equals(token.AccessToken, replacing, StringComparison.Ordinal);

        if (renewed && token!.IsUsableAt(DateTimeOffset.UtcNow, margin))
        {
            logger.LogInformation(
                "Renewed the authorization for MCP server {Server}; the new access token runs to {ExpiresAt}.",
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

        // Three different failures, and they are told apart here rather than lumped together, because the advice
        // that follows from each is different and two of the three are actively misled by the other's. A renewal
        // that worked and produced a token too short for the margin asked of it is not an expired sign-in: nothing
        // expired, and signing in again yields another token exactly like it.
        var reason = renewed
            ? McpOAuthAttentionReason.TokenTooShortLived
            : unreachable
                ? McpOAuthAttentionReason.ServerUnreachable
                : McpOAuthAttentionReason.SignInExpired;

        if (renewed)
        {
            logger.LogWarning(
                "MCP server {Server} renewed to an access token that expires at {ExpiresAt}, sooner than the {Margin} this use has to hold it for.",
                server.Name,
                token!.ExpiresAt,
                margin);
        }

        // An interactive failure is already in front of the operator — they pressed the button and the dialog is
        // waiting on it — so it is not reported a second time through the channel that exists for the automatic path.
        return interactive
            ? McpOAuthAccess.AuthorizationRequired with { SignInStage = stage, Reason = reason }
            : _Unauthorized(server, reason, stage);
    }

    /// <summary>
    /// One silent renewal per server at a time (see <see cref="_renewals"/>). Callers that arrive while one runs
    /// wait for its outcome rather than starting a second, and then re-read the store for themselves.
    /// </summary>
    private Task<HandshakeOutcome> _SharedRenewalAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        Task<HandshakeOutcome> renewal;
        lock (_renewalsLock)
        {
            if (!_renewals.TryGetValue(server.IdentityKey, out var running))
            {
                running = _RenewAndClearAsync(server);
                _renewals[server.IdentityKey] = running;
            }

            renewal = running;
        }

        // Each caller's own deadline applies to its wait and not to the shared work: the first arrival's budget must
        // not decide the fate of everyone who queued behind it.
        return renewal.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Runs the renewal on its own thread-pool work item and removes it from <see cref="_renewals"/> as the very
    /// last thing it does, before anyone can observe the returned task as complete.
    /// <para>
    /// Both halves matter. Running off-thread keeps <see cref="_renewalsLock"/> held for nothing longer than the
    /// dictionary check-and-set — <see cref="_RenewAsync"/>'s synchronous prefix (building the OAuth options, which
    /// does a blocking loopback-port probe) must never run while that lock is held, or every caller queued behind it
    /// on a busy machine blocks for as long as the renewal takes rather than for a dictionary lookup. And folding the
    /// removal into the same task — rather than a detached continuation, which used to clear the slot on its own
    /// schedule — closes the gap where a caller could find the slot already empty (renewal done, cleanup not yet
    /// run) and start a needless second renewal, or find it still occupied by a task whose answer had already gone
    /// stale (cleanup done, completion not yet observed) and wait on that instead of starting its own. Measured, not
    /// theoretical: both shapes flaked in CI (<c>McpOAuthSessionMarginTests</c>) before this fix — a TTY launch's
    /// five-second abandon budget is exactly the load that used to make the slower shape ordinary rather than rare.
    /// </para>
    /// </summary>
    private Task<HandshakeOutcome> _RenewAndClearAsync(McpServerConfig server) => Task.Run(async () =>
    {
        try
        {
            return await _RenewAsync(server).ConfigureAwait(false);
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
    private async Task<HandshakeOutcome> _RenewAsync(McpServerConfig server)
    {
        try
        {
            // CancellationToken.None on purpose: this work is shared, so honouring the first caller's token would
            // let whoever happened to arrive first cancel a renewal the others are still waiting on.
            return await _HandshakeAsync(server, interactive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Renewing the authorization for MCP server {Server} ended unexpectedly.", server.Name);
            return new HandshakeOutcome(McpSignInStage.NoBrowserLaunched, Explained: false, Unreachable: _IsUnreachable(exception));
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

    // Says it once. The same failure repeats on every request the proxy forwards and on every session start, and an
    // instruction repeated per request is noise the operator learns to scroll past — so the line is written on the
    // way into a state and not again while nothing has changed. Read and write under one lock: two requests arriving
    // together on a server that just went stale would otherwise both find nothing recorded and both announce it.
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
            _NotifyOperator(server, guidance);
        }

        return McpOAuthAccess.AuthorizationRequired with { SignInStage = stage, Reason = reason };
    }

    /// <summary>
    /// Puts the same sentence where the operator will actually meet it. A log line is not a notification: the way
    /// this failure was found in the first place was a server silently missing from a session, and a trail that only
    /// exists once you know to go looking for it does not fix that.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget with the failure caught inside: a toast that cannot be shown must never take down the credential
    /// path it is reporting on, and an unobserved fault on a path nothing awaits is a silence of its own. Only reached
    /// from the announce branch above, so the once-per-transition rule covers this as well as the log line.
    /// </remarks>
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

        // A token that can still be renewed counts as signed in whatever its clock says: the next use renews it
        // without the operator noticing, so asking them to sign in again would be asking for nothing. Without a
        // refresh token the same margin applies as on the credential path — a token with a minute left is one a
        // session start will already refuse, and a status that says "signed in" about it is wrong about exactly the
        // case this exists to make visible.
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

    // Connects far enough to make the SDK complete whatever OAuth step is outstanding, then drops the client again,
    // and answers how far a sign-in got (AC-457) so the caller can say where it stopped without being handed the
    // reason it stopped. Deliberately its own minimal transport rather than the tool provider's: that one overlays
    // built-in servers and session tokens, and depending on it from here would put a cycle in the graph the tool
    // provider already sits in.
    // <c>Explained</c> says whether this already wrote the operator's line, so the caller can supply one when it did
    // not rather than write a second next to it. <c>Unreachable</c> separates "the server said no" from "the server
    // said nothing" (AC-524): only the first is something signing in again can fix, and telling an operator to sign
    // in while the host is down is advice that cannot work.
    private async Task<HandshakeOutcome> _HandshakeAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken)
    {
        if (server.Transport != McpTransport.Http || string.IsNullOrWhiteSpace(server.Url))
        {
            // No address to reach is a configuration gap, not an outage — an operator who is told to wait for the
            // server to come back would wait forever.
            return new HandshakeOutcome(McpSignInStage.NoBrowserLaunched, Explained: false, Unreachable: false);
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

            var clientOptions = interactive ? McpInteractiveOAuthClientOptions.Value : null;
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

            return new HandshakeOutcome(stageRecorder.Reached, Explained: true, Unreachable: _IsUnreachable(exception));
        }
        catch (Exception exception)
        {
            // An expected outcome, not an anomaly: with nobody to ask, this is exactly what a refusal to hand a
            // sign-in to a browser looks like. The caller decides what it means by re-reading the store.
            logger.LogInformation(exception, "Could not renew authorization for MCP server {Server} without asking the operator.", server.Name);
            return new HandshakeOutcome(stageRecorder.Reached, Explained: false, Unreachable: _IsUnreachable(exception));
        }

        return new HandshakeOutcome(stageRecorder.Reached, Explained: false, Unreachable: false);
    }

    // Whether the handshake failed because nothing answered, rather than because what answered refused. Walked down
    // the inner exceptions because the MCP client wraps a transport failure in its own type, so the socket error that
    // says "down" is never the one that surfaces.
    private static bool _IsUnreachable(Exception exception) =>
        exception is HttpRequestException or SocketException or TimeoutException
        || (exception.InnerException is { } inner && _IsUnreachable(inner));

    /// <param name="Stage">How far a sign-in got, for telling the operator where it stopped (AC-457).</param>
    /// <param name="Explained">Whether the handshake already wrote the operator's line, so the caller supplies one only when it did not.</param>
    /// <param name="Unreachable">Whether nothing answered, as opposed to something answering with a refusal (AC-524).</param>
    private readonly record struct HandshakeOutcome(McpSignInStage Stage, bool Explained, bool Unreachable);
}
