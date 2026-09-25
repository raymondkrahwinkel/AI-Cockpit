using NSubstitute;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Kind.Tests;

// AC-1394 acceptance 2: the backend part mounts its MCP endpoint from Initialize alone, with no Avalonia type
// anywhere in this test — the same guarantee BackendPluginStartupTests proves for the bundled plugins, here for a
// plugin that is not bundled (it is a store install, never embedded in Cockpit.App).
public class KindMcpEndpointMountTests
{
    [Fact]
    public void Initialize_MountsTheKindMcpEndpoint()
    {
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(new FakePluginStorage());
        var sessions = Substitute.For<ICockpitSessionObserver>();
        sessions.OpenSessions.Returns([]);
        host.Sessions.Returns(sessions);

        using var plugin = new KindPlugin();
        plugin.Initialize(host);

        _ = host.Received(1).AddMcpEndpoint("cockpit-kind", Arg.Any<object>(), Arg.Any<Func<bool>?>());
    }
}
