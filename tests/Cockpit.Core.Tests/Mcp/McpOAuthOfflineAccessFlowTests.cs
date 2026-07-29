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
    private static McpServerConfig _Server(string url, string? oauthScopes = null) => new()
    {
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
            // authorizer's own loopback listener, exactly as an operator completing consent would.
            BrowserOpener = url =>
            {
                _ = Task.Run(() => new HttpClient().GetAsync(url));
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

        await using var client = await McpClient.CreateAsync(transport);

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
        Assert.Equal("test-refresh-token", stored.RefreshToken);
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

        await _RunFlowAsync(server, oauthScopes: "depot custom-scope");

        Assert.Equal("depot custom-scope", server.LastRequestedScope);
    }
}
