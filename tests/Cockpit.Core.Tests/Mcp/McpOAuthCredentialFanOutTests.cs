using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Tests.Claude;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
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

    private static IMcpOAuthCoordinator _CoordinatorAnswering(McpOAuthAccess access)
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
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
    public async Task SdkSession_AsksNonInteractively_SoStartingASessionNeverOpensABrowser()
    {
        var inner = new FakePluginSessionDriver();
        var coordinator = _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken));
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, AuthKey, _CatalogOf(OAuthServer), oauthCoordinator: coordinator);

        await adapter.StartAsync();

        await coordinator.Received().AcquireAsync(Arg.Any<McpServerConfig>(), false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SdkSession_ForAnApiKeyServer_IsUnchanged_AndIsNeverAskedForAnOAuthToken()
    {
        var inner = new FakePluginSessionDriver();
        var apiKeyServer = new McpServerConfig
        {
            Name = "youtrack",
            Transport = McpTransport.Http,
            Url = "http://127.0.0.1:9000/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "yt-pat-value",
        };
        var coordinator = _CoordinatorAnswering(McpOAuthAccess.NotRequired);
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, AuthKey, _CatalogOf(apiKeyServer), oauthCoordinator: coordinator);

        await adapter.StartAsync();

        Assert.NotNull(inner.LastMcpServers);
        Assert.Equal("yt-pat-value", Assert.Single(inner.LastMcpServers).BearerToken);
        await coordinator.DidNotReceive().AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
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
        coordinator.Received().AcquireAsync(
            Arg.Any<McpServerConfig>(),
            false,
            Arg.Is<CancellationToken>(token => token.CanBeCanceled));
    }

    [Fact]
    public void TtyLaunch_WhenTheRenewalOutlastsTheBudget_LeavesTheServerOut()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.AcquireAsync(Arg.Any<McpServerConfig>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<Task<McpOAuthAccess>>(_ => throw new OperationCanceledException());
        var (adapter, inner) = _TtyAdapter(coordinator);

        adapter.BuildLaunch(_TtyContext());

        // A renewal that runs past the budget is a server without a credential, not a launch that fails.
        Assert.Empty(_LaunchContextOf(inner).McpServers ?? []);
    }

    private static (PluginTtySessionProviderAdapter Adapter, IPluginTtyProvider Inner) _TtyAdapter(IMcpOAuthCoordinator coordinator)
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "claude", [], new Dictionary<string, string?>(), "/wd", []));

        return (new PluginTtySessionProviderAdapter(
            "claude-provider.claude",
            inner,
            """{"Command":"claude"}""",
            _CatalogOf(OAuthServer),
            coordinator), inner);
    }

    private static TtyLaunchContext _TtyContext() =>
        new(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>());

    private static PluginTtyLaunchContext _LaunchContextOf(IPluginTtyProvider inner) =>
        inner.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<PluginTtyLaunchContext>()
            .Single();
}
