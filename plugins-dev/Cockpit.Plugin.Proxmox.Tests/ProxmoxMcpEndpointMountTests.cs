using NSubstitute;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Proxmox.Tests;

// AC-1394 acceptance 2: the backend part mounts its MCP endpoint from Initialize alone, with no Avalonia type
// anywhere in this test — the same guarantee BackendPluginStartupTests proves for the bundled plugins, here for a
// plugin that is not bundled (it is a store install, never embedded in Cockpit.App).
public class ProxmoxMcpEndpointMountTests
{
    [Fact]
    public void Initialize_MountsTheProxmoxMcpEndpoint()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());

        using var plugin = new ProxmoxPlugin();
        plugin.Initialize(host);

        _ = host.Received(1).AddMcpEndpoint("cockpit-proxmox", Arg.Any<object>(), Arg.Any<Func<bool>?>());
    }
}
