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
}
