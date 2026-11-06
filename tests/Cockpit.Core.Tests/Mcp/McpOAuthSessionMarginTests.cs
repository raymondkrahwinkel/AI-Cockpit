using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Mcp;

// Per-request OAuth token lifetime and coalesced renewal of rotating refresh grants.
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
    public async Task Acquire_AgainstAServerWhoseTokensAreShorterThanTheRenewalLead_DoesNotRenewOnEveryCall()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromMinutes(5));
        var coordinator = new McpOAuthCoordinator(store, authorizer, new CapturingLogger<McpOAuthCoordinator>());

        for (var call = 0; call < 3; call++)
        {
            Assert.Equal(McpAuthState.Authorized, (await coordinator.AcquireAsync(_Server(), interactive: false)).State);
        }

        // A five-minute token never clears the ten-minute lead, so a lead applied without qualification would spend a
        // refresh grant on every single call — and these servers rotate, so that is a replayed-grant storm, not churn.
        Assert.Equal(1, authorizer.Attempts);
    }

    [Fact]
    public async Task Acquire_WhenSeveralCallsStartAtOnce_RenewsOnceAndGivesThemAllTheSameToken()
    {
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", _TokenExpiringIn(TimeSpan.FromMinutes(-1), refreshToken: "refresh"));
        var authorizer = new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromHours(1));
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        // Held closed so the first renewal is genuinely still in flight while the other seven arrive. Without a real
        // window the callers would file past one after another and the test would pass on a race that never ran.
        authorizer.Gate.Reset();
        var starts = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => coordinator.AcquireAsync(_Server(), interactive: false)))
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
}
