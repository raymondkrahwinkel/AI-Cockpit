using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// <see cref="PluginTtySessionProviderAdapter"/>: the seam between the core's <see cref="ITtySessionProvider"/>
/// vocabulary and a plugin's smaller <see cref="IPluginTtyProvider"/> one (#45 fase B2) — everything in
/// <see cref="TtyLaunchContext"/> reaches the plugin, everything in the plugin's <see cref="PluginTtyLaunchSpec"/>
/// reaches back out as a <see cref="TtyLaunchSpec"/>, and <see cref="SessionResume"/>'s three cases collapse
/// into the plugin's two ("resume this one, or the last one" vs. "nothing" — a plugin has no reason to see
/// the core's own "start fresh" case, since that already reads as no resume at all).
/// </summary>
public class PluginTtySessionProviderAdapterTests
{
    [Theory]
    [InlineData(true, "Assistant")]
    [InlineData(false, null)]
    public void BuildLaunch_NarrowsTheRegistryToThePerSessionSelection_SoAnUncheckedServerNeverReachesThePlugin(bool includeAgentName, string? expectedName)
    {
        // Two eligible registry servers; the operator's checklist only ticked "docker" for this session. Before the
        // fix the TTY route fanned the whole registry, so "youtrack" reached the CLI despite being unchecked (#44).
        var catalog = Substitute.For<Cockpit.Core.Abstractions.Mcp.IMcpServerCatalog>();
        catalog.GetServersForProjectAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new List<Cockpit.Core.Mcp.McpServerConfig>
        {
            new() { Name = "docker", Transport = Cockpit.Core.Mcp.McpTransport.Http, Url = "http://127.0.0.1:1/mcp", IncludeAgentName = includeAgentName },
            new() { Name = "youtrack", Transport = Cockpit.Core.Mcp.McpTransport.Http, Url = "http://127.0.0.1:2/mcp" },
        });

        var inner = Substitute.For<IPluginTtyProvider>();
        inner.BuildLaunch(Arg.Any<PluginTtyLaunchContext>()).Returns(new PluginTtyLaunchSpec(
            "codex", [], new Dictionary<string, string?>(), "/wd", []));
        var adapter = new PluginTtySessionProviderAdapter("cli-agent-provider.codex", inner, """{"Command":"codex"}""", catalog);

        var profile = new Cockpit.Core.Profiles.SessionProfile("Assistant", new Cockpit.Core.Profiles.PluginProviderConfig("test", "{}"));
        var context = new TtyLaunchContext(profile, new Dictionary<string, string>(), "/wd", null, new Dictionary<string, string>())
        {
            EnabledMcpServerNames = new HashSet<string> { "docker" },
        };

        adapter.BuildLaunch(context);

        inner.Received(1).BuildLaunch(Arg.Is<PluginTtyLaunchContext>(pluginContext =>
            pluginContext.McpServers.Count == 1 && pluginContext.McpServers[0].Name == "docker"
            && pluginContext.McpServers[0].Headers.GetValueOrDefault("X-Cockpit-Agent") == expectedName));
    }

}
