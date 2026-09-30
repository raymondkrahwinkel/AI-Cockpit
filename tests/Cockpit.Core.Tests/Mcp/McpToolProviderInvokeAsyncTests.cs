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

    // --- callerFallbackServers (AC-499) ---------------------------------------------------------------------------
    // PluginBackendHost hands this an additive candidate list scoped to the calling plugin's own contributions — see its
    // own remarks on _OwnMcpServerContributions. These tests exercise only the mechanism this class owns: the
    // catalog is tried first, the fallback list only when that finds nothing under the name, and a name absent from
    // both never resolves — never that the caller was entitled to what is in the list, which is PluginBackendHost's job.

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
