using NSubstitute;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Docker.Tests;

// AC-1394 acceptance 2: the backend part mounts its MCP endpoint from Initialize alone, with no Avalonia type
// anywhere in this test — the same guarantee BackendPluginStartupTests proves for the bundled plugins, here for a
// plugin that is not bundled (it is a store install, never embedded in Cockpit.App).
public class DockerMcpEndpointMountTests
{
    [Fact]
    public void Initialize_MountsTheDockerMcpEndpoint()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());

        using var plugin = new DockerPlugin();
        plugin.Initialize(host);

        _ = host.Received(1).AddMcpEndpoint("cockpit-docker", Arg.Any<object>(), Arg.Any<Func<bool>?>());
    }
}
