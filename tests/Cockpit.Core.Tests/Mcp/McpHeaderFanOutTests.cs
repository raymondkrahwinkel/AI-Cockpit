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
/// The wiring for AC-354: an operator's custom headers actually reach a spawned agent. The rule itself is covered by
/// <see cref="McpAgentHeadersTests"/> and each provider's own config tests; what is proven here is the step between
/// them, which nothing else touches — remove the mapping from either adapter and only these go red.
/// </summary>
public class McpHeaderFanOutTests
{
    private static readonly McpAuthKey AuthKey = new();

    private static McpServerConfig ServerWithHeader => new()
    {
        Name = "private-api",
        Transport = McpTransport.Http,
        Url = "https://api.example/mcp",
        Headers = [new McpHeader("X-Api-Key", "the-key")],
    };

    private static IMcpServerCatalog _CatalogOf(McpServerConfig server)
    {
        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<McpServerConfig> { server });
        return catalog;
    }

    [Fact]
    public async Task SdkSession_CarriesTheOperatorsHeadersToTheAgent()
    {
        var inner = new FakePluginSessionDriver();
        var adapter = new PluginSessionDriverAdapter(inner, inner.Capabilities, AuthKey, _CatalogOf(ServerWithHeader));

        await adapter.StartAsync();

        Assert.NotNull(inner.LastMcpServers);
        Assert.Equal("the-key", Assert.Single(inner.LastMcpServers).Headers["X-Api-Key"]);
    }

    [Fact]
    public void TtyLaunch_CarriesTheOperatorsHeadersToTheAgent()
    {
        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "claude", [], new Dictionary<string, string?>(), "/wd", []));
        var adapter = new PluginTtySessionProviderAdapter(
            "claude-provider.claude", inner, """{"Command":"claude"}""", _CatalogOf(ServerWithHeader));

        adapter.BuildLaunch(new TtyLaunchContext(null, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>()));

        var context = inner.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<PluginTtyLaunchContext>()
            .Single();
        Assert.NotNull(context.McpServers);
        Assert.Equal("the-key", Assert.Single(context.McpServers).Headers["X-Api-Key"]);
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
