using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Tests.Claude;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The point of AC-353: a server the cockpit signed in to reaches a spawned agent <em>with</em> its credential.
/// <para>
/// Both spawn routes are covered here because they are separate code that must agree — the SDK route resolves its
/// servers asynchronously, the TTY route synchronously, and until this ticket neither carried an OAuth credential at
/// all. What each provider then does with <see cref="PluginMcpServer.BearerToken"/> — an <c>Authorization</c> header
/// for Claude, an environment variable for Codex, a headers array for Kimi — is locked by those providers' own
/// config tests; this is the seam that feeds all three.
/// </para>
/// </summary>
public class McpOAuthCredentialFanOutTests
{
    private static readonly McpAuthKey AuthKey = new();

    private const string AccessToken = "depot-access-token";

    private static McpServerConfig OAuthServer => new()
    {
        Name = "depot",
        Transport = McpTransport.Http,
        Url = "https://depot.example/mcp",
        Auth = McpServerAuth.OAuth,
    };

    private static McpServerConfig ApiKeyServer => new()
    {
        Name = "youtrack",
        Transport = McpTransport.Http,
        Url = "http://127.0.0.1:9000/mcp",
        Auth = McpServerAuth.ApiKey,
        ApiKey = "yt-pat-value",
    };

    private static IMcpServerCatalog _CatalogOf(params McpServerConfig[] servers)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(servers.ToList());
        return catalog;
    }

    // Both spawn routes ask through AcquireForSessionAsync (AC-524) — the entry point that keeps the wide margin a
    // session needs. AcquireAsync is stubbed alongside it so a route that regressed to the per-request entry point
    // fails on the assertion that names it, rather than on a substitute answering "NotRequired" for free.
    private static IMcpOAuthCoordinator _CoordinatorAnswering(McpOAuthAccess access)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireForSessionAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(access);
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(access);
        return coordinator;
    }

    [Fact]
    public async Task SdkSession_ForAnAuthorizedOAuthServer_CarriesTheTokenToTheAgent()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(OAuthServer),
            oauthCoordinator: _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken)));

        await adapter.StartAsync();

        Assert.NotNull(inner.LastMcpServers);
        var server = Assert.Single(inner.LastMcpServers);
        Assert.Equal("depot", server.Name);
        Assert.Equal(AccessToken, server.BearerToken);
    }

    [Fact]
    public async Task SdkSession_ForAnOAuthServerNobodySignedInTo_LeavesItOutOfTheSession()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(OAuthServer),
            oauthCoordinator: _CoordinatorAnswering(McpOAuthAccess.AuthorizationRequired));

        await adapter.StartAsync();

        // Not handed over bare. An address the agent cannot authenticate to is not a server it can use, and passing
        // it along only moves the refusal into the agent's own client — the "401 from the depths" this is meant to
        // end. The operator gets a warning in its place.
        Assert.NotNull(inner.LastMcpServers);
        Assert.Empty(inner.LastMcpServers);
    }

    [Fact]
    public async Task SdkSession_AsksThroughTheSessionEntryPoint_SoTheTokenIsNotOneThatDiesMinutesIn()
    {
        var inner = new FakePluginSessionDriver();
        var coordinator = _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken));
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, AuthKey, _CatalogOf(OAuthServer), oauthCoordinator: coordinator);

        await adapter.StartAsync();

        // AC-524: the per-request entry point keeps a two-minute margin, which is right for a token spent
        // immediately and wrong for one a session holds for hours. Asking through the session entry point is what
        // makes the difference, and it is never interactive — so this also carries the old promise that starting a
        // session never opens a browser.
        await coordinator.Received().AcquireForSessionAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SdkSession_ForAnApiKeyServer_IsUnchanged_AndIsNeverAskedForAnOAuthToken()
    {
        var inner = new FakePluginSessionDriver();
        var coordinator = _CoordinatorAnswering(McpOAuthAccess.NotRequired);
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, AuthKey, _CatalogOf(ApiKeyServer), oauthCoordinator: coordinator);

        await adapter.StartAsync();

        Assert.NotNull(inner.LastMcpServers);
        Assert.Equal("yt-pat-value", Assert.Single(inner.LastMcpServers).BearerToken);
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await coordinator.DidNotReceive().AcquireForSessionAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TtyLaunch_ForAnAuthorizedOAuthServer_CarriesTheTokenToTheAgent()
    {
        var (adapter, inner) = _TtyAdapter(_CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken)));

        adapter.BuildLaunch(_TtyContext());

        var servers = _LaunchContextOf(inner).McpServers;
        Assert.NotNull(servers);
        Assert.Equal(AccessToken, Assert.Single(servers).BearerToken);
    }

    [Fact]
    public void TtyLaunch_ForAnOAuthServerNobodySignedInTo_LeavesItOutOfTheLaunch()
    {
        var (adapter, inner) = _TtyAdapter(_CoordinatorAnswering(McpOAuthAccess.AuthorizationRequired));

        adapter.BuildLaunch(_TtyContext());

        Assert.Empty(_LaunchContextOf(inner).McpServers ?? []);
    }

    [Fact]
    public void TtyLaunch_BoundsHowLongItWaitsForARenewal()
    {
        var coordinator = _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken));
        var (adapter, _) = _TtyAdapter(coordinator);

        adapter.BuildLaunch(_TtyContext());

        // This path is synchronous out to the launcher and is reached from the UI thread, so an unbounded wait is a
        // frozen application. A cancellable token is the evidence that a budget was put on it; CancellationToken.None
        // would mean the launch is willing to wait forever.
        coordinator.Received().AcquireForSessionAsync(
            Arg.Any<McpServerConfig>(),
            Arg.Is<CancellationToken>(token => token.CanBeCanceled));
    }

    [Fact]
    public void TtyLaunch_WhenTheRenewalOutlastsTheBudget_LeavesOutOnlyThatServer()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireForSessionAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpOAuthAccess>>(_ => throw new OperationCanceledException());
        var (adapter, inner) = _TtyAdapter(coordinator, OAuthServer, ApiKeyServer);

        adapter.BuildLaunch(_TtyContext());

        // The second server is what makes this test mean anything. Without the catch, the cancellation reaches
        // _ResolveRegistry's blanket handler and the launch loses the *whole* registry — which, with only the OAuth
        // server in the fixture, looks exactly like the intended "skip this one" and leaves the guard unproven.
        var servers = _LaunchContextOf(inner).McpServers;
        Assert.NotNull(servers);
        Assert.Equal("youtrack", Assert.Single(servers).Name);
    }

    [Fact]
    public async Task SdkSession_WhenTheServerIsProxied_WritesTheLoopbackAddressAndNoTokenAtAll()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(OAuthServer),
            oauthCoordinator: _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken)),
            oauthProxy: _ProxyAnswering("http://127.0.0.1:54321/mcp"));

        await adapter.StartAsync();

        Assert.NotNull(inner.LastMcpServers);
        var server = Assert.Single(inner.LastMcpServers);

        // AC-524: the session is pointed at the cockpit's own address, and the credential it carries is the
        // COCKPIT_MCP_KEY env reference every cockpit-hosted endpoint uses (CockpitHosted is what makes the config
        // writer emit that instead of a literal). So no OAuth token is written to disk at all — which also takes it
        // out of reach of any other process on this machine that knows the config's path.
        Assert.Equal("http://127.0.0.1:54321/mcp", server.Url);
        Assert.True(server.CockpitHosted);
        Assert.Null(server.BearerToken);
    }

    [Fact]
    public async Task SdkSession_WhenTheProxyCannotBeMounted_FallsBackToTheTokenRatherThanLosingTheServer()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(OAuthServer),
            oauthCoordinator: _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken)),
            oauthProxy: _ProxyAnswering(null));

        await adapter.StartAsync();

        // Degraded, not broken. A listener that would not bind is a reason to write the token as before — with the
        // session margin behind it — rather than to drop a server the operator is signed in to.
        Assert.NotNull(inner.LastMcpServers);
        var server = Assert.Single(inner.LastMcpServers);
        Assert.Equal("https://depot.example/mcp", server.Url);
        Assert.Equal(AccessToken, server.BearerToken);
        Assert.False(server.CockpitHosted);
    }

    [Fact]
    public async Task SdkSession_WhenTheProxyIsMounted_AsksOnlyWhetherASignInExists()
    {
        var inner = new FakePluginSessionDriver();
        var coordinator = _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken));
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(OAuthServer),
            oauthCoordinator: coordinator,
            oauthProxy: _ProxyAnswering("http://127.0.0.1:54321/mcp"));

        await adapter.StartAsync();

        // Behind the endpoint the session never holds a token, so demanding one that outlasts the session would
        // refuse a server that works perfectly — a server whose tokens live ten minutes is entirely usable here,
        // because the endpoint fetches a new one on every call.
        await coordinator.DidNotReceive().AcquireForSessionAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>());
        await coordinator.Received().AcquireAsync(Arg.Any<McpServerConfig>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SdkSession_WhenTheProxyIsGoneAndTheTokensAreTooShortLived_LeavesTheServerOutRatherThanBakingInAFailure()
    {
        // Port 1 refuses instantly, so the connect after the renewal fails fast; the address has to be the one the
        // stored token names, or the origin check discards the token and this measures "never signed in" instead of
        // the short-lifetime rule it is here for.
        var server = OAuthServer with { Url = "http://127.0.0.1:1/mcp" };
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync(
            server.IdentityKey,
            server.Name,
            new McpOAuthToken
            {
                AccessToken = "about-to-die",
                RefreshToken = "refresh",
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ResourceUrl = server.Url,
            });

        // The real coordinator, deliberately: a substitute that always answers "Authorized" cannot reach this path
        // at all, which is how it stayed uncovered. This one renews for real (into a ten-minute token) and applies
        // its own margins to the result.
        var coordinator = new McpOAuthCoordinator(
            store,
            new RenewingMcpOAuthAuthorizer(store, TimeSpan.FromMinutes(10)),
            NullLogger<McpOAuthCoordinator>.Instance);

        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(server),
            oauthCoordinator: coordinator,
            oauthProxy: _ProxyAnswering(null));

        await adapter.StartAsync();

        // With no endpoint in front of it the token goes into the config and stays there, so a ten-minute token is a
        // session that loses this server in ten minutes — the exact defect this ticket was opened for, reintroduced
        // through the fallback. Better no server, with the reason said out loud, than one that is guaranteed to fail
        // while the operator is working.
        Assert.NotNull(inner.LastMcpServers);
        Assert.Empty(inner.LastMcpServers);

        // The check that this measured the right thing: with the proxy present the very same setup keeps the server,
        // because there the ten-minute lifetime is irrelevant. Without this, an origin mismatch or a missing token
        // would produce the assertion above just as well.
        var withProxy = new FakePluginSessionDriver();
        await new PluginSessionDriverAdapter(
            withProxy,
            withProxy.Capabilities,
            AuthKey,
            _CatalogOf(server),
            oauthCoordinator: coordinator,
            oauthProxy: _ProxyAnswering("http://127.0.0.1:54321/mcp")).StartAsync();

        Assert.NotNull(withProxy.LastMcpServers);
        Assert.Equal("http://127.0.0.1:54321/mcp", Assert.Single(withProxy.LastMcpServers).Url);
    }

    [Fact]
    public void TtyLaunch_WhenTheServerIsProxied_WritesTheLoopbackAddressAndNoTokenAtAll()
    {
        var (adapter, inner) = _TtyAdapter(
            _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken)),
            _ProxyAnswering("http://127.0.0.1:54321/mcp"));

        adapter.BuildLaunch(_TtyContext());

        var servers = _LaunchContextOf(inner).McpServers;
        Assert.NotNull(servers);
        var server = Assert.Single(servers);
        Assert.Equal("http://127.0.0.1:54321/mcp", server.Url);
        Assert.True(server.CockpitHosted);
        Assert.Null(server.BearerToken);
    }

    [Fact]
    public void TtyLaunch_WhenTheRenewalOutlastsTheBudget_SaysTheServerDidNotAnswerInTime()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireForSessionAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpOAuthAccess>>(_ => throw new OperationCanceledException());
        var logger = new CapturingLogger<PluginTtySessionProviderAdapter>();
        var (adapter, _) = _TtyAdapter(coordinator, oauthProxy: null, logger);

        adapter.BuildLaunch(_TtyContext());

        // A renewal that ran out of the launch's budget is the server not answering in time, and the advice for that
        // is to wait rather than to sign in again. Without a cause on the answer, the line below falls through to the
        // sentence for "no reason given" — which tells the operator nothing they can act on.
        Assert.Contains(logger.Messages, message => message.Contains("could not be reached", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("press Sign in", StringComparison.Ordinal));
    }

    private static IMcpOAuthProxy _ProxyAnswering(string? proxyUrl)
    {
        var proxy = Substitute.For<IMcpOAuthProxy>();
        proxy.MountAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(proxyUrl);
        return proxy;
    }

    private static (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _TtyAdapter(
        IMcpOAuthCoordinator coordinator,
        params McpServerConfig[] servers) => _TtyAdapter(coordinator, oauthProxy: null, logger: null, servers);

    private static (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _TtyAdapter(
        IMcpOAuthCoordinator coordinator,
        IMcpOAuthProxy? oauthProxy,
        params McpServerConfig[] servers) => _TtyAdapter(coordinator, oauthProxy, logger: null, servers);

    private static (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _TtyAdapter(
        IMcpOAuthCoordinator coordinator,
        IMcpOAuthProxy? oauthProxy,
        ILogger<PluginTtySessionProviderAdapter>? logger,
        params McpServerConfig[] servers)
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "claude", [], new Dictionary<string, string?>(), "/wd", []));

        return (new PluginTtySessionProviderAdapter(
            "claude-provider.claude",
            inner,
            """{"Command":"claude"}""",
            _CatalogOf(servers.Length == 0 ? [OAuthServer] : servers),
            coordinator,
            logger,
            oauthProxy: oauthProxy), inner);
    }

    private static TtyLaunchContext _TtyContext() =>
        new(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>());

    private static PluginTtyLaunchContext _LaunchContextOf(IPluginTtyProvider inner) =>
        inner.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<PluginTtyLaunchContext>()
            .Single();
}
