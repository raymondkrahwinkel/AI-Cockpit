using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Tests.Claude;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
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

    private static IMcpOAuthProxy _ProxyAnswering(string? proxyUrl)
    {
        var proxy = Substitute.For<IMcpOAuthProxy>();
        proxy.MountAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(proxyUrl);
        return proxy;
    }

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
