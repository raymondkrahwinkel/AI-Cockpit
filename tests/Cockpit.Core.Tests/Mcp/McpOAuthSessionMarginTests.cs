using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The margin a session's credential is judged by, and the single-flight gate that keeps making it wider from
/// costing the authorization it protects (AC-524).
/// <para>
/// The failure this exists for was measured, not imagined: a session started while its access token still had eleven
/// minutes to live, the token was written into a config the CLI reads exactly once, and eleven minutes later the
/// server and all its tools were gone from that session with no way back. The per-request margin is two minutes,
/// which is right for a token spent immediately and useless for one held for hours.
/// </para>
/// </summary>
public class McpOAuthSessionMarginTests
{
    // Port 1 refuses immediately, so the connect after the (faked) renewal fails fast and deterministically.
    private const string ServerUrl = "http://127.0.0.1:1/mcp";

    private static McpServerConfig _Server() => new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = ServerUrl,
        Auth = McpServerAuth.OAuth,
    };

    private static McpOAuthToken _TokenExpiringIn(TimeSpan remaining, string accessToken = "stored-access-token", string? refreshToken = null) => new()
    {
        AccessToken = accessToken,
        RefreshToken = refreshToken,
        ExpiresAt = DateTimeOffset.UtcNow.Add(remaining),
        ResourceUrl = ServerUrl,
    };

    [Fact]
    public async Task AcquireForSession_ForATokenWithElevenMinutesLeft_RefusesToHandItToASession()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(11)));
        var coordinator = new McpOAuthCoordinator(store, new FakeMcpOAuthAuthorizer(), NullLogger<McpOAuthCoordinator>.Instance);

        var forSession = await coordinator.AcquireForSessionAsync(_Server());
        var forOneRequest = await coordinator.AcquireAsync(_Server(), interactive: false);

        // The two answers differing is the whole fix. Eleven minutes is plenty for the request that is about to go
        // out and nowhere near enough for a config a session reads once and then holds — and with no refresh token
        // there is nothing to renew from, so the session is told up front instead of losing the server later.
        Assert.Equal(McpAuthState.AuthorizationRequired, forSession.State);
        Assert.Equal(McpOAuthAttentionReason.SignInExpired, forSession.Reason);
        Assert.Equal(McpAuthState.Authorized, forOneRequest.State);
        Assert.Equal(McpOAuthAttentionReason.None, forOneRequest.Reason);
    }

    [Fact]
    public async Task AcquireForSession_ForATokenThatOutlivesTheMargin_UsesItWithoutRenewing()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromHours(6)));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireForSessionAsync(_Server());

        // The other side of the margin, and the reason it is a fixed number rather than "always renew": a server
        // handing out long-lived tokens costs nothing at all, so widening the margin buys the short-lived case
        // without making every session start pay a token-endpoint round trip.
        Assert.Equal("stored-access-token", access.AccessToken);
        Assert.Equal(0, authorizer.Attempts);
    }

    [Fact]
    public async Task AcquireForSession_ForAStaleTokenThatCanRenew_RenewsItSilentlyRatherThanAskingTheOperator()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var logger = new CapturingLogger<McpOAuthCoordinator>();
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));
        var coordinator = new McpOAuthCoordinator(store, authorizer, logger);

        var access = await coordinator.AcquireForSessionAsync(_Server());

        // The ladder, in order: an expired token with a refresh grant behind it is renewed without a word to the
        // operator. Asking someone to sign in while a usable refresh token is sitting in the store is a defect, not
        // caution — so nothing here is allowed to reach them.
        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(authorizer.LastIssuedToken, access.AccessToken);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task AcquireForSession_WhenEveryTokenThisServerIssuesIsShorterThanTheMargin_SaysSoRatherThanHandingOneOver()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromMinutes(10));
        var logger = new CapturingLogger<McpOAuthCoordinator>();

        var access = await new McpOAuthCoordinator(store, authorizer, logger).AcquireForSessionAsync(_Server());

        // The edge a wide margin creates: this server's tokens live ten minutes, so no token it will ever issue
        // clears the fifty-five a session asks for. Handing it over anyway would produce exactly the session this
        // ticket exists to stop — one that loses the server ten minutes in — so the answer is no, and it is the
        // caller (which knows whether the loopback endpoint is standing in and the lifetime therefore irrelevant)
        // that decides what to do with it.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);

        // The reason is the assertion that matters. Nothing expired here and nothing was revoked: the renewal
        // worked. Reporting SignInExpired would send the operator through a browser sign-in that hands back another
        // ten-minute token, which is advice that cannot work.
        Assert.Equal(McpOAuthAttentionReason.TokenTooShortLived, access.Reason);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("press Sign in", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcquireForSession_WhenTheRenewalItselfFailed_IsNotReportedAsAShortLivedToken()
    {
        var store = new FakeMcpOAuthTokenStore();
        var stale = _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh");
        await store.SaveAsync("depot", "depot", stale);

        // FakeMcpOAuthAuthorizer writes nothing, so the store still holds the same expired token afterwards — the
        // shape every failed renewal leaves behind. That leftover looks exactly like a fresh token that came out too
        // short (present, for this address, inside the margin), and the only thing telling the two apart is whether
        // the value changed. Port 1 refuses the connection, so the honest verdict here is that nothing answered.
        var access = await new McpOAuthCoordinator(store, new FakeMcpOAuthAuthorizer(), NullLogger<McpOAuthCoordinator>.Instance)
            .AcquireForSessionAsync(_Server());

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Equal(McpOAuthAttentionReason.ServerUnreachable, access.Reason);
    }

    [Fact]
    public async Task AcquireForSession_WhenSeveralSessionsStartAtOnce_RenewsOnceAndGivesThemAllTheSameToken()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        // Held closed so the first renewal is genuinely still in flight while the other seven arrive. Without a real
        // window the callers would file past one after another and the test would pass on a race that never ran.
        authorizer.Gate.Reset();
        var starts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => coordinator.AcquireForSessionAsync(_Server())))
            .ToArray();

        Assert.True(authorizer.Started.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        authorizer.Gate.Set();
        var results = await Task.WhenAll(starts);

        // The authorization servers in use here rotate refresh tokens: a renewal issues a new one and redeems the
        // old, so a second renewal presenting that same old token is a replayed grant — which a server may answer by
        // revoking the whole authorization. Making renewals more frequent is exactly what makes eight at once
        // ordinary, so without this gate the fix would cause the outage it was built to remove.
        Assert.Equal(1, authorizer.Attempts);
        Assert.All(results, access => Assert.Equal(McpAuthState.Authorized, access.State));
        Assert.Single(results.Select(access => access.AccessToken).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RenewRejected_WhenTheServerRefusedATokenTheClockSaysIsFine_RenewsItAnyway()
    {
        var store = new FakeMcpOAuthTokenStore();
        var refused = _TokenExpiringIn(TimeSpan.FromHours(6), accessToken: "revoked-at-the-far-end", refreshToken: "refresh");
        await store.SaveAsync("depot", "depot", refused);
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));
        var logger = new CapturingLogger<McpOAuthCoordinator>();

        var access = await new McpOAuthCoordinator(store, authorizer, logger).RenewRejectedAsync(_Server(), refused.AccessToken);

        // Six hours left by this cockpit's clock, and dead at the server — a grant revoked at the far end or a
        // rotation race lost to another session looks exactly like this. Every margin here would have said "fine",
        // so without the server's own verdict the same token would go out on every later call for six hours.
        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(authorizer.LastIssuedToken, access.AccessToken);
        Assert.Equal(1, authorizer.Attempts);
        Assert.Contains(logger.Messages, message => message.Contains("still considered valid until", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RenewRejected_WithNothingToRenewFrom_StillReportsAnExpiredSignIn()
    {
        var store = new FakeMcpOAuthTokenStore();
        var refused = _TokenExpiringIn(TimeSpan.FromHours(6), accessToken: "revoked-at-the-far-end");
        await store.SaveAsync("depot", "depot", refused);
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));

        var access = await new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance)
            .RenewRejectedAsync(_Server(), refused.AccessToken);

        // The other half of AC-550, and the half a careless fix loses: softening every refusal into "could not be
        // confirmed, try again" would bury the one case where signing in again is exactly what is needed. Here the
        // server refused the token and there is no refresh grant behind it, so nothing can renew it and no retry
        // will ever produce a different answer — this genuinely is a sign-in that has to be made anew.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
        Assert.Equal(McpOAuthAttentionReason.SignInExpired, access.Reason);
        Assert.Equal(0, authorizer.Attempts);
    }

    [Fact]
    public async Task RenewRejected_ForATokenSomebodyElseAlreadyReplaced_HandsBackTheCurrentOneWithoutRenewing()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromHours(6), accessToken: "the-one-that-replaced-it", refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));

        var access = await new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance)
            .RenewRejectedAsync(_Server(), "the-token-that-was-refused");

        // The ordinary shape of a burst: many calls in flight against a credential the server has just started
        // refusing, one of them renews, and the rest arrive holding a token that is no longer the stored one. Giving
        // them the current one is what keeps the burst to a single round trip instead of one per call.
        Assert.Equal("the-one-that-replaced-it", access.AccessToken);
        Assert.Equal(0, authorizer.Attempts);
    }

    [Fact]
    public async Task RenewRejected_WhenEveryCallIsRefusedAtOnce_StillRenewsOnlyOnce()
    {
        var store = new FakeMcpOAuthTokenStore();
        var refused = _TokenExpiringIn(TimeSpan.FromHours(6), accessToken: "revoked-at-the-far-end", refreshToken: "refresh");
        await store.SaveAsync("depot", "depot", refused);
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        authorizer.Gate.Reset();
        var refusals = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => coordinator.RenewRejectedAsync(_Server(), refused.AccessToken)))
            .ToArray();

        Assert.True(authorizer.Started.Wait(TimeSpan.FromSeconds(10)));
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        authorizer.Gate.Set();
        var results = await Task.WhenAll(refusals);

        // A server that starts refusing does so for every call in flight at that moment, not for one — so this path
        // has to go through the same gate as the margin-driven renewal. Eight refusals redeeming the same rotating
        // refresh token eight times is how a fix for a lost session becomes a lost authorization.
        Assert.Equal(1, authorizer.Attempts);
        Assert.All(results, access => Assert.Equal(McpAuthState.Authorized, access.State));
        Assert.Single(results.Select(access => access.AccessToken).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AcquireForSession_AfterARenewalHasFinished_RenewsAgainWhenTheNextTokenGoesStale()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromMinutes(-1));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        await coordinator.AcquireForSessionAsync(_Server());
        await coordinator.AcquireForSessionAsync(_Server());

        // The gate coalesces renewals; it must not remember one. A slot left occupied after the work finished would
        // make the second expiry join a task that already completed and hand back the stale token forever.
        Assert.Equal(2, authorizer.Attempts);
    }
}
