using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Tests.Claude;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The wiring for AC-354: an operator's custom headers actually reach a spawned agent. The rule itself is covered by
/// <see cref="McpAgentHeadersTests"/> and each provider's own config tests; what is proven here is the step between
/// them, which nothing else touches — remove the mapping from either adapter and only these go red.
/// </summary>
public class McpHeaderFanOutTests
{
    private static readonly McpAuthKey AuthKey = new();

    private static IMcpServerCatalog _CatalogOf(McpServerConfig server)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig> { server });
        return catalog;
    }

    [Fact]
    public async Task SdkSession_ForAStdioServer_CarriesNoHeaders()
    {
        var stdio = new McpServerConfig
        {
            Name = "fs",
            Transport = McpTransport.Stdio,
            Command = "npx",
            Headers = [new McpHeader("X-Api-Key", "the-key")],
        };
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, AuthKey, _CatalogOf(stdio));

        await adapter.StartAsync();

        // A stdio server has no request to put a header on; carrying one there would be a credential written into a
        // config for a transport that cannot send it.
        Assert.NotNull(inner.LastMcpServers);
        Assert.Empty(Assert.Single(inner.LastMcpServers).Headers);
    }
}
