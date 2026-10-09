using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpToolProvider.InvokeAsync"/> (AC-502) — the app's own <see cref="IMcpToolInvoker"/>, called for a
/// plugin's project-editor picker (a Depot connection listing its projects) rather than a session's tool-loop. Real
/// in-process MCP servers, the same idiom <see cref="McpToolProviderConnectAsyncTests"/> already uses, so the
/// success/failure paths exercise an actual handshake and tool call rather than a mocked transport.
/// </summary>
public class McpToolProviderInvokeAsyncTests
{
    [Fact]
    public async Task InvokeAsync_DisabledServer_ReturnsFailed_NotTreatedAsUnknownlessEnabled()
    {
        await using var server = await InProcessMcpHttpServer.StartAsync<McpTestToolInvoke>();
        var provider = _ProviderFor([
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = server.Url, Enabled = false },
        ]);

        var result = await provider.InvokeAsync("server-a", "echo");

        Assert.Equal(McpToolInvocationOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task InvokeAsync_CallsTheToolAndReturnsItsTextContent()
    {
        await using var server = await InProcessMcpHttpServer.StartAsync<McpTestToolInvoke>();
        var provider = _ProviderFor([
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = server.Url },
        ]);

        var result = await provider.InvokeAsync("server-a", "echo", new Dictionary<string, object?> { ["text"] = "hello" });

        Assert.Equal(McpToolInvocationOutcome.Success, result.Outcome);
        Assert.Equal("hello", result.Content);
    }

    // AC-1517, reproduced against a local Depot first: a plugin's background call on a sign-in that can no longer be
    // renewed opened the operator's browser on the authorize page. It must name the server in the cockpit instead.
    [Fact]
    public async Task InvokeAsync_OAuthServerWhoseRefreshGrantIsDead_OpensNoBrowser_AndAnnouncesTheServer()
    {
        await using var server = await InProcessOAuthMcpServer.StartAsync(advertiseOfflineAccess: true);
        var config = new McpServerConfig { Id = "depot", Name = "Depot: Work", Transport = McpTransport.Http, Url = server.Url, Auth = McpServerAuth.OAuth };
        var store = new FakeMcpOAuthTokenStore();
        await store.SaveAsync("depot", config.Name, new McpOAuthToken
        {
            AccessToken = "already-expired",
            RefreshToken = "a-refresh-token-this-server-never-issued",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ResourceUrl = server.Url,
            ClientId = InProcessOAuthMcpServer.ClientId,
            AuthorizationServer = server.BaseUrl,
        });

        var browserOpened = new List<Uri>();
        var authorizer = new McpOAuthAuthorizer(NullLogger<McpOAuthAuthorizer>.Instance, store)
        {
            BrowserOpener = uri =>
            {
                browserOpened.Add(uri);
                return false;
            },
        };
        var coordinator = new McpOAuthCoordinator(store, authorizer, NullLogger<McpOAuthCoordinator>.Instance);
        var announced = new List<string>();
        coordinator.SignInNeeded += needing => announced.Add(needing.Name);
        var provider = _ProviderFor([config], coordinator, authorizer);

        var result = await provider.InvokeAsync(config.Name, "echo");

        Assert.Empty(browserOpened);
        Assert.Equal(McpToolInvocationOutcome.AuthorizationRequired, result.Outcome);
        Assert.Equal(["Depot: Work"], announced);
    }

    // --- callerFallbackServers (AC-499) ---------------------------------------------------------------------------
    // PluginBackendHost hands this an additive candidate list scoped to the calling plugin's own contributions — see its
    // own remarks on _OwnMcpServerContributions. These tests exercise only the mechanism this class owns: the
    // catalog is tried first, the fallback list only when that finds nothing under the name, and a name absent from
    // both never resolves — never that the caller was entitled to what is in the list, which is PluginBackendHost's job.

    private static McpToolProvider _ProviderFor(IEnumerable<McpServerConfig> registry, IMcpOAuthCoordinator? oauthCoordinator = null, IMcpOAuthAuthorizer? oauthAuthorizer = null)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        return new McpToolProvider(
            catalog,
            oauthAuthorizer ?? Substitute.For<IMcpOAuthAuthorizer>(),
            oauthCoordinator ?? Substitute.For<IMcpOAuthCoordinator>(),
            new McpAuthKey(),
            new SessionMcpKeyring(),
            NullLogger<McpToolProvider>.Instance);
    }
}
