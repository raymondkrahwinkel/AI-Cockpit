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
    public async Task SdkSession_ForAnOAuthServerNobodySignedInTo_CarriesNoCredential()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(
            inner,
            inner.Capabilities,
            AuthKey,
            _CatalogOf(OAuthServer),
            oauthCoordinator: _CoordinatorAnswering(McpOAuthAccess.AuthorizationRequired));

        await adapter.StartAsync();

        // The server is still handed over — it is the operator's choice to have selected it — but without a
        // credential invented for it. What must not happen is a stale or empty string passing for one.
        Assert.NotNull(inner.LastMcpServers);
        var server = Assert.Single(inner.LastMcpServers);
        Assert.Null(server.BearerToken);
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
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "claude", [], new Dictionary<string, string?>(), "/wd", []));
        var adapter = new PluginTtySessionProviderAdapter(
            "claude-provider.claude",
            inner,
            """{"Command":"claude"}""",
            _CatalogOf(OAuthServer),
            _CoordinatorAnswering(McpOAuthAccess.Authorized(AccessToken)));

        adapter.BuildLaunch(new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>()));

        var context = inner.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<PluginTtyLaunchContext>()
            .Single();
        Assert.NotNull(context.McpServers);
        var server = Assert.Single(context.McpServers);
        Assert.Equal(AccessToken, server.BearerToken);
    }

    [Fact]
    public void TtyLaunch_ForAnOAuthServerNobodySignedInTo_CarriesNoCredential()
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "claude", [], new Dictionary<string, string?>(), "/wd", []));
        var adapter = new PluginTtySessionProviderAdapter(
            "claude-provider.claude",
            inner,
            """{"Command":"claude"}""",
            _CatalogOf(OAuthServer),
            _CoordinatorAnswering(McpOAuthAccess.AuthorizationRequired));

        adapter.BuildLaunch(new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>()));

        var context = inner.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<PluginTtyLaunchContext>()
            .Single();
        Assert.NotNull(context.McpServers);
        Assert.Null(Assert.Single(context.McpServers).BearerToken);
    }
}
