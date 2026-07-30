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
    public async Task InvokeAsync_UnknownServer_ReturnsFailed()
    {
        var provider = _ProviderFor([]);

        var result = await provider.InvokeAsync("nonexistent", "echo");

        Assert.Equal(McpToolInvocationOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

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

    [Fact]
    public async Task InvokeAsync_TheToolItselfFails_ReturnsFailed_NeverThrows()
    {
        await using var server = await InProcessMcpHttpServer.StartAsync<McpTestToolInvoke>();
        var provider = _ProviderFor([
            new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = server.Url },
        ]);

        var result = await provider.InvokeAsync("server-a", "boom");

        Assert.Equal(McpToolInvocationOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task InvokeAsync_UnreachableServer_ReturnsFailed()
    {
        var provider = _ProviderFor([
            new McpServerConfig { Name = "server-fail", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" },
        ]);

        var result = await provider.InvokeAsync("server-fail", "echo");

        Assert.Equal(McpToolInvocationOutcome.Failed, result.Outcome);
    }

    // AC-134/AC-502: never pop an interactive browser sign-in from this path — the same rule
    // McpToolProviderConnectAsyncTests pins for EnumerateServerToolsAsync's own pre-flight read, applied here since
    // this path is also reached from a UI click, not a session start the operator is already present for.
    [Fact]
    public async Task InvokeAsync_OAuthServerNeedingSignIn_ReturnsAuthorizationRequired_WithoutConnecting()
    {
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.AuthorizationRequired);
        var provider = _ProviderFor(
            [new McpServerConfig { Name = "server-oauth", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth }],
            oauthCoordinator: coordinator);

        var result = await provider.InvokeAsync("server-oauth", "echo");

        Assert.Equal(McpToolInvocationOutcome.AuthorizationRequired, result.Outcome);
    }

    // --- callerFallbackServers (AC-499) ---------------------------------------------------------------------------
    // CockpitHost hands this an additive candidate list scoped to the calling plugin's own contributions — see its
    // own remarks on _OwnMcpServerContributions. These tests exercise only the mechanism this class owns: the
    // catalog is tried first, the fallback list only when that finds nothing under the name, and a name absent from
    // both never resolves — never that the caller was entitled to what is in the list, which is CockpitHost's job.

    [Fact]
    public async Task InvokeAsync_UnknownToCatalog_PresentInCallerFallback_CallsTheToolAndReturnsItsTextContent()
    {
        await using var server = await InProcessMcpHttpServer.StartAsync<McpTestToolInvoke>();
        var provider = _ProviderFor([]);
        var fallback = new List<McpServerConfig> { new() { Name = "own-server", Transport = McpTransport.Http, Url = server.Url } };

        var result = await provider.InvokeAsync("own-server", "echo", new Dictionary<string, object?> { ["text"] = "hi" }, callerFallbackServers: fallback);

        Assert.Equal(McpToolInvocationOutcome.Success, result.Outcome);
        Assert.Equal("hi", result.Content);
    }

    [Fact]
    public async Task InvokeAsync_UnknownToCatalog_NotInCallerFallbackEither_ReturnsFailed()
    {
        // The fallback list is a scoped candidate set, not a wildcard: a name absent from it (here, a name
        // belonging to a different, unentitled server) still fails to resolve.
        var provider = _ProviderFor([]);
        var fallback = new List<McpServerConfig> { new() { Name = "own-server", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" } };

        var result = await provider.InvokeAsync("someone-elses-server", "echo", callerFallbackServers: fallback);

        Assert.Equal(McpToolInvocationOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task InvokeAsync_NameKnownToBothCatalogAndCallerFallback_TheCatalogEntryWins()
    {
        // The catalog is consulted first; the fallback is only a rescue for a name the catalog has nothing under —
        // proven here by pointing the fallback entry of the same name at an address nothing listens on, so the call
        // can only have succeeded via the catalog's own (real, reachable) entry.
        await using var server = await InProcessMcpHttpServer.StartAsync<McpTestToolInvoke>();
        var provider = _ProviderFor([new McpServerConfig { Name = "server-a", Transport = McpTransport.Http, Url = server.Url }]);
        var fallback = new List<McpServerConfig> { new() { Name = "server-a", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp" } };

        var result = await provider.InvokeAsync("server-a", "echo", new Dictionary<string, object?> { ["text"] = "hi" }, callerFallbackServers: fallback);

        Assert.Equal(McpToolInvocationOutcome.Success, result.Outcome);
    }

    [Fact]
    public async Task InvokeAsync_OAuthServerOnlyInCallerFallback_ReturnsAuthorizationRequired_WithoutConnecting()
    {
        // The OAuth short-circuit above the connect (AC-134/AC-502) has to apply the same way to a fallback-resolved
        // config as to a catalog one — this is what a fallback-resolved Depot connection would hit in practice.
        var coordinator = Substitute.For<IMcpOAuthCoordinator>();
        coordinator.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(McpAuthState.AuthorizationRequired);
        var provider = _ProviderFor([], oauthCoordinator: coordinator);
        var fallback = new List<McpServerConfig>
        {
            new() { Name = "own-oauth-server", Transport = McpTransport.Http, Url = "http://127.0.0.1:1/mcp", Auth = McpServerAuth.OAuth },
        };

        var result = await provider.InvokeAsync("own-oauth-server", "echo", callerFallbackServers: fallback);

        Assert.Equal(McpToolInvocationOutcome.AuthorizationRequired, result.Outcome);
    }

    private static McpToolProvider _ProviderFor(IEnumerable<McpServerConfig> registry, IMcpOAuthCoordinator? oauthCoordinator = null)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(registry.ToList());
        return new McpToolProvider(
            catalog,
            Substitute.For<IMcpOAuthAuthorizer>(),
            oauthCoordinator ?? Substitute.For<IMcpOAuthCoordinator>(),
            new McpAuthKey(),
            new SessionMcpKeyring(),
            NullLogger<McpToolProvider>.Instance);
    }
}
