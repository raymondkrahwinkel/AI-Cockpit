using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.AddToolbarAction"/> (AC-91): a plugin's Sessions-toolbar action reaches the running UI
/// through the contribution sink, tagged with the plugin id — the same wiring as the other status-bar/menu
/// contributions — so the toolbar can render its button (and #72 order/hide can apply).
/// </summary>
public class CockpitHostToolbarActionTests
{
    [Fact]
    public void AddToolbarAction_ForwardsToTheContributionSink_TaggedWithThePluginId()
    {
        var sink = Substitute.For<IPluginContributionSink>();
        ICockpitHost host = _BuildHost(sink);
        var action = new ToolbarAction("Docker settings", MaterialIconKind.Docker, () => Task.CompletedTask);

        host.AddToolbarAction(action);

        sink.Received(1).AddToolbarAction("docker", action);
    }

    private static CockpitHost _BuildHost(IPluginContributionSink sink) =>
        new(
            "docker",
            "Docker",
            new ServiceCollection().BuildServiceProvider(),
            sink,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);
}
