using NSubstitute;
using Cockpit.Plugin.Kind;
using Cockpit.Plugin.Kubernetes;
using Cockpit.Plugin.Proxmox;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Docker.Tests;

// AC-1394 acceptance 2: Docker, Kind, Kubernetes and Proxmox each mount their MCP endpoint from Initialize alone
// — the same guarantee BackendPluginStartupTests proves for the bundled plugins, here for four plugins that are
// not bundled. One Theory over the four, hosted in this project rather than duplicated in each of theirs.
public class IntegrationPluginMcpEndpointMountTests
{
    [Theory]
    [InlineData(typeof(DockerPlugin), "cockpit-docker")]
    [InlineData(typeof(KindPlugin), "cockpit-kind")]
    [InlineData(typeof(KubernetesPlugin), "cockpit-k8s")]
    [InlineData(typeof(ProxmoxPlugin), "cockpit-proxmox")]
    public void Initialize_MountsTheMcpEndpoint(Type pluginType, string serverName)
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        var sessions = Substitute.For<ICockpitSessionObserver>();
        sessions.OpenSessions.Returns([]);
        host.Sessions.Returns(sessions);

        using var plugin = (ICockpitPlugin)(Activator.CreateInstance(pluginType)
            ?? throw new InvalidOperationException($"{pluginType} has no parameterless constructor."));
        plugin.Initialize(host);

        _ = host.Received(1).AddMcpEndpoint(serverName, Arg.Any<object>(), Arg.Any<Func<bool>?>());
    }
}
