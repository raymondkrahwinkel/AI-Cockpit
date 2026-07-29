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
