using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// AC-505: end-to-end proof that Cockpit's OAuth wiring actually comes away with a refresh token when the
/// authorization server advertises <c>offline_access</c> — and neither invents one nor breaks when it doesn't.
/// Drives a real <see cref="HttpClientTransport"/> + <see cref="McpClient.CreateAsync"/> handshake against
/// <see cref="InProcessOAuthMcpServer"/> rather than asserting on <see cref="McpOAuthAuthorizer.CreateOptions"/>
/// alone: on ModelContextProtocol.Core 1.4.1 the SDK silently ignored a configured <c>Scopes</c> list whenever
/// the protected-resource metadata's own <c>scopes_supported</c> was non-empty (as Depot's measured one is) —
/// a unit test on the options object would have stayed green while the real flow stayed broken. 2.0.0 fixes
/// this natively (SEP-2207): a refresh token is requested from the authorization-server metadata, never the
/// narrower protected-resource one.
/// </summary>
public class McpOAuthOfflineAccessFlowTests
{
    // A bounded lifetime for the fire-and-forget redirect GET below: a broken fixture must fail this test with a
    // clear timeout rather than hang on HttpClient's 100-second default, or on a listener that never unblocks.
    private static readonly HttpClient _BrowserHttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private static McpServerConfig _Server(string url, string? oauthScopes = null) => new()
    {
        Id = "depot",
        Name = "depot",
        Transport = McpTransport.Http,
        Url = url,
        Auth = McpServerAuth.OAuth,
        OAuthScopes = oauthScopes,
    };

    private static async Task<FakeMcpOAuthTokenStore> _RunFlowAsync(InProcessOAuthMcpServer server, string? oauthScopes = null)
    {
        var store = new FakeMcpOAuthTokenStore();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store)
        {
            // Stands in for the desktop hand-off: nothing opens a real browser in a test run, so this drives the
            // redirect itself — a plain HTTP GET that follows the fake authorize endpoint's 302 straight into the
            // authorizer's own loopback listener, exactly as an operator completing consent would. Failures are
            // swallowed here on purpose: the outer timeout below is what turns "nothing arrived" into a clear
            // test failure, rather than an unobserved task exception on a background thread.
            BrowserOpener = url =>
            {
                _ = _BrowserHttpClient.GetAsync(url).ContinueWith(_ => { }, TaskScheduler.Default);
                return true;
            },
        };

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "depot",
            Endpoint = new Uri(server.Url),
            TransportMode = HttpTransportMode.AutoDetect,
            OAuth = authorizer.CreateOptions(_Server(server.Url, oauthScopes), interactive: true),
        });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);

        return store;
    }

    [Fact]
    public async Task Flow_AgainstAServerThatAdvertisesOfflineAccess_EndsWithARefreshToken()
    {
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);

        var store = await _RunFlowAsync(server);

        var stored = await store.GetAsync("depot");
        Assert.NotNull(stored);
        Assert.Equal(InProcessOAuthMcpServer.AccessToken, stored!.AccessToken);

        // AC1 + AC5 (red-without-fix): on ModelContextProtocol.Core 1.4.1 this is null — the SDK derives the
        // requested scope from the protected-resource metadata's own scopes_supported ("depot" only, matching the
        // live Depot measurement) and never falls through to what the authorization server itself advertises.
        Assert.Equal(InProcessOAuthMcpServer.RefreshToken, stored.RefreshToken);
    }

    [Fact]
    public async Task Flow_AgainstAServerThatDoesNotAdvertiseOfflineAccess_StillAuthorizes_WithoutOne()
    {
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: false);

        var store = await _RunFlowAsync(server);

        // AC2 (regression): unchanged behaviour for a server that never offered offline_access — authorization
        // still succeeds, and nothing is invented that the server never advertised.
        var stored = await store.GetAsync("depot");
        Assert.NotNull(stored);
        Assert.Equal(InProcessOAuthMcpServer.AccessToken, stored!.AccessToken);
        Assert.Null(stored.RefreshToken);
    }

    [Fact]
    public async Task Flow_WithAPerServerScopesOverride_RequestsExactlyThoseScopes_IgnoringTheServersOwnAdvertisement()
    {
        // Neither document advertises "custom-scope" — proving this came from the override, not from anything
        // scopes_supported offered. AC3: a per-server scopes setting overrides the derivation.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);

        var store = await _RunFlowAsync(server, oauthScopes: "depot custom-scope");

        Assert.Equal("depot custom-scope", server.LastRequestedScope);

        // A deliberate trade-off, pinned rather than left implicit: ScopeSelector (which the override is built on)
        // replaces the SDK's candidate list outright — it runs after offline_access was already appended to it.
        // Leaving offline_access out of an override therefore loses it, on a server that would otherwise have
        // granted it. The escape hatch exists for a server with its own requirements; an operator who reaches for
        // it and still wants a refresh token has to name offline_access themselves.
        var stored = await store.GetAsync("depot");
        Assert.Null(stored?.RefreshToken);
    }

    [Fact]
    public async Task Acquire_NonInteractively_RenewsAStaleAccessToken_UsingAStoredRefreshToken()
    {
        // AC4: the point of getting a refresh token in the first place — McpOAuthCoordinator.AcquireAsync renews
        // without asking the operator, against a real refresh grant rather than an assumption that storing a
        // refresh token is enough on its own.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        // ClientId/AuthorizationServer are what makes the refresh token below usable at all: the SDK only attempts
        // a refresh grant once it has a client identity to present, and a fresh connect attempt (this one) starts a
        // brand-new provider with none — it restores this pairing from the stored token instead of running dynamic
        // client registration again. Standing in for what a real sign-in would have persisted alongside the token.
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "already-expired",
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: false);

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.RenewedAccessToken, access.AccessToken);
        Assert.Equal(InProcessOAuthMcpServer.RenewedAccessToken, (await store.GetAsync("depot"))?.AccessToken);
    }

    [Fact]
    public async Task Acquire_NonInteractively_ForATokenInsideTheMarginButStillAlive_ActuallyRenewsIt()
    {
        // AC-771, end to end against a real token endpoint rather than a faked seam — because the defect lived
        // precisely on the seam a fake would have stood in for.
        //
        // The stored token has ninety seconds left: spent by the coordinator's two-minute request margin, and alive
        // by the SDK's, which has no margin at all (`TokenContainer.IsExpired` is `UtcNow >= ObtainedAt + ExpiresIn`).
        // It is also a token this server accepts, so nothing forces a refresh from the far end either. Without the
        // margin reaching the SDK, the connect below succeeds on the token already held, the store comes back
        // unchanged, and the coordinator reads that as a renewal that failed — which reached the agent as
        // "could not renew its sign-in" on an ordinary call, for the last two minutes of every token's life.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(90),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: false);

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.RenewedAccessToken, access.AccessToken);

        // Exactly one, from the far side's own count: the fix has to make the renewal happen, not make it happen
        // repeatedly — a refresh grant this server rotates is not something to spend twice on one call.
        Assert.Equal(1, server.RefreshAttempts);
    }

    [Fact]
    public async Task AcquireForSession_ForATokenInsideTheSessionMarginButStillAlive_ActuallyRenewsIt()
    {
        // The same defect on the wider margin, and the more damaging of the two: a session keeps its credential for
        // hours, so it asks for fifty-five minutes of life. A one-hour token therefore failed this from five minutes
        // old onwards — fifty-five of every sixty minutes — and a session that met it started without the server.
        // Invisible so far only because the loopback endpoint (AC-524) puts most servers on the request margin.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireForSessionAsync(_Server(server.Url));

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.RenewedAccessToken, access.AccessToken);
        Assert.Equal(1, server.RefreshAttempts);
    }

    [Fact]
    public async Task Acquire_WhenTheTokenEndpointIsHavingABadMinute_KeepsServingOnTheTokenInHand()
    {
        // The grace period, and the reason the renewal is started ten minutes out rather than two: a token endpoint
        // that is down — Depot restarting, a slow minute, a lost packet — must not cost the call. Five minutes left
        // is past the point where renewing begins and well clear of the two minutes a call needs, so the honest
        // answer is to use what is held and try again on the next call.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        server.FailNextRefreshes(5);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: false);

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.AccessToken, access.AccessToken);

        // It really did try, which is what makes this a grace period rather than a margin quietly ignored — the next
        // call tries again, and one of them lands long before the credential runs out.
        Assert.True(server.RefreshAttempts >= 1);
    }

    [Fact]
    public async Task Acquire_WhenTheGraceRanOutAndTheEndpointIsStillFailing_StopsServingRatherThanPretending()
    {
        // The other end of the same grace period. One minute left is inside what a call needs to survive its own
        // round trip, so there is nothing honest left to hand over — and an agent told "send it again" while the
        // credential is genuinely dying would keep sending it into the same wall.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        server.FailNextRefreshes(5);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: false);

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);

        // A 500 from the token endpoint is not the authorization server declaring the grant dead, so it must not be
        // reported as one (AC-646) — the agent is told to send it again, not to go and sign in.
        Assert.Equal(McpOAuthAttentionReason.RenewalCouldNotBeConfirmed, access.Reason);
    }

    [Fact]
    public async Task AcquireForSession_WhenTheTokenEndpointIsHavingABadMinute_StillRefusesATokenerThatWillNotOutlastTheSession()
    {
        // The asymmetry, pinned: the same failure that a single call rides out must stop a session start. This
        // answer goes into a config the session reads once and holds for hours, so a token with half an hour left is
        // the session that loses its server halfway through — which is the whole of AC-524.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        server.FailNextRefreshes(5);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireForSessionAsync(_Server(server.Url));

        Assert.Equal(McpAuthState.AuthorizationRequired, access.State);
    }

    [Fact]
    public async Task RenewRejected_ForATokenTheServerRefuses_RenewsThroughTheRealSdk_NotJustTheClock()
    {
        // The third route into the same method, and the only one whose renewal does not come from the margin at all:
        // here the token has half an hour on our clock and the server refuses it anyway (a grant revoked at the far
        // end, or a rotation race lost). The margin subtracted above leaves it alive, so what has to drive the
        // refresh is the server's own 401 during the handshake — an assumption about the SDK's behaviour, and this
        // ticket exists because assumptions about the SDK's behaviour went three tickets without being measured.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = "a-token-this-server-no-longer-honours",
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.RenewRejectedAsync(_Server(server.Url), "a-token-this-server-no-longer-honours");

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.RenewedAccessToken, access.AccessToken);
    }

    [Fact]
    public async Task Acquire_NonInteractively_ForATokenWithRoomToSpare_SpendsNoRefreshGrantAtAll()
    {
        // The mirror, and the one that would make this fix worse than the defect: a margin applied to every read
        // would rotate the refresh grant on every connect. The authorization servers in use here rotate on refresh,
        // so that is not churn but a replayed grant waiting to happen — the outage `_renewals` exists to prevent.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = InProcessOAuthMcpServer.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: false);

        // Thirty minutes clears the two-minute request margin, so the stored token is handed over as it stands and
        // the token endpoint is never called.
        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.AccessToken, access.AccessToken);
        Assert.Equal(0, server.RefreshAttempts);
    }

    [Fact]
    public async Task Acquire_Interactively_SurvivesABrowserConsentThatTakesLongerThanTheDefaultDiscoverProbeTimeout()
    {
        // AC-505 follow-up (2026-07-29, live-verified against production Depot): ModelContextProtocol.Core 2.0.0
        // added McpClientOptions.DiscoverProbeTimeout (5s default) around the same HTTP call whose 401 the SDK
        // turns into this interactive sign-in — so on an unmodified McpClientOptions, an operator who takes more
        // than about five seconds to see the browser tab and click consent has the whole flow cancelled out from
        // under them (confirmed live: the loopback listener's GetContextAsync is torn down mid-wait, and the SDK
        // reports "AuthorizationCallbackHandler returned a null authorization result"). Red without the coordinator's
        // widened McpClientOptions: a 6-second delay before the redirect is enough to reproduce it deterministically.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store)
        {
            BrowserOpener = url =>
            {
                _ = Task.Delay(TimeSpan.FromSeconds(6))
                    .ContinueWith(_ => _BrowserHttpClient.GetAsync(url), TaskScheduler.Default);
                return true;
            },
        };
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: true, timeout.Token);

        Assert.Equal(McpAuthState.Authorized, access.State);
        Assert.Equal(InProcessOAuthMcpServer.AccessToken, access.AccessToken);
    }
}
