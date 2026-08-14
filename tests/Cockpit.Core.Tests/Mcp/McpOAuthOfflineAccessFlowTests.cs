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
        // AC-771, against a real token endpoint because the defect lived on the seam a fake would have stood in for.
        // Ninety seconds left is spent by the two-minute request margin and alive by the SDK's, which has none — and
        // this server accepts the token, so nothing forces a refresh from the far end either.
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
        // The same defect on the wider margin, and the more damaging of the two: a one-hour token failed this from
        // five minutes old onwards, and a session that met it started without the server. Invisible so far only
        // because the loopback endpoint (AC-524) puts most servers on the request margin instead.
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
        // The grace period, and why renewing starts ten minutes out rather than two: a token endpoint that is down
        // must not cost the call. Five minutes left is past the point where renewing begins and well clear of the
        // two a call needs, so the honest answer is to use what is held and try again next time.
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

    [Theory]
    [InlineData(5, McpAuthState.Authorized, null)]
    [InlineData(1, McpAuthState.AuthorizationRequired, McpOAuthAttentionReason.SignInExpired)]
    public async Task Acquire_WhenTheGrantItselfIsRevoked_SaysSoOnlyOnceTheGraceHasRunOut(
        int minutesLeft,
        McpAuthState expectedState,
        McpOAuthAttentionReason? expectedReason)
    {
        // A dead grant, not a bad minute: an unknown refresh token makes this endpoint answer `invalid_grant`. The
        // grace period covers the call while there is life left, so the operator hears about a revoked sign-in later
        // than before — and must still hear about it, with the right sentence, the moment it stops covering.
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", "depot", new McpOAuthToken
        {
            AccessToken = InProcessOAuthMcpServer.AccessToken,
            RefreshToken = "a-refresh-token-this-server-has-revoked",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(minutesLeft),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store);
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);

        var access = await coordinator.AcquireAsync(_Server(server.Url), interactive: false);

        Assert.Equal(expectedState, access.State);
        if (expectedReason is { } reason)
        {
            Assert.Equal(reason, access.Reason);
        }
    }

    [Fact]
    public async Task RenewRejected_ForATokenTheServerRefuses_RenewsThroughTheRealSdk_NotJustTheClock()
    {
        // The third route into the same method, and the only one whose renewal does not come from the margin: half
        // an hour left on our clock, refused by the server anyway, so the refresh has to come from its own 401
        // during the handshake. Measured rather than assumed — assuming it is what cost this ticket three rounds.
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
