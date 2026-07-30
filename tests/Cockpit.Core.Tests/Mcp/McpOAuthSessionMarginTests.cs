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
        Assert.Equal(McpAuthState.Authorized, forOneRequest.State);
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
    public async Task AcquireForSession_WhenEveryTokenThisServerIssuesIsShorterThanTheMargin_UsesItAnyway()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromMinutes(10));
        var logger = new CapturingLogger<McpOAuthCoordinator>();

        var access = await new McpOAuthCoordinator(store, authorizer, logger).AcquireForSessionAsync(_Server());

        // The edge a wide margin creates. This server's tokens live ten minutes, so no token it will ever issue can
        // clear the fifty-five the margin asks for — honouring it literally would make the server permanently
        // unavailable, which is strictly worse than the short-lived credential this ticket set out to replace. The
        // shortfall goes on the record instead.
        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(authorizer.LastIssuedToken, access.AccessToken);
        Assert.Contains(logger.Messages, message => message.Contains("sooner than the", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcquireForSession_WhenTheRenewedTokenCannotEvenSurviveOneRequest_LeavesTheServerUnauthorized()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromSeconds(20));

        var access = await new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance).AcquireForSessionAsync(_Server());

        // The floor above is a floor, not a shrug: twenty seconds does not survive the round trip it would be spent
        // on either, so accepting it would only move the failure into the first tool call.
        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
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
