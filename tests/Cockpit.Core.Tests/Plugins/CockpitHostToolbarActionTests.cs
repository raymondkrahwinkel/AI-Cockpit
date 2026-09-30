using Material.Icons;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitUiHost.AddToolbarAction"/> (AC-91): a plugin's Sessions-toolbar action reaches the running UI
/// through the contribution sink, tagged with the plugin id — the same wiring as the other status-bar/menu
/// contributions — so the toolbar can render its button (and #72 order/hide can apply).
/// </summary>
public class CockpitHostToolbarActionTests
{
    [Fact]
    public void AddToolbarAction_ForwardsToTheContributionSink_TaggedWithThePluginId()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        ICockpitUiHost host = _BuildHost(sink);
        var action = new ToolbarAction("Docker settings", MaterialIconKind.Docker, () => Task.CompletedTask);

        host.AddToolbarAction(action);

        sink.Received(1).AddToolbarAction("docker", action);
    }

    private static ICockpitUiHost _BuildHost(IPluginContributionSink sink) =>
        TestUiHost.Create(sink: sink, pluginId: "docker", pluginName: "Docker");
}
