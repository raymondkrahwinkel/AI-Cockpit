using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

// The second half of loading Diagram as PluginManager/PluginUiManager do — InitializeUi after Initialize, on a UI
// host whose channel is the same hub the test host's backend Channel publishes on, so windows reach the registries.
// F2.13/AC-1401: Diagram's UI part is its own assembly now (uiAssembly/uiEntryType in plugin.json), activated the
// same way PluginUiManager.ActivateUi loads any plugin's UI part in production — this used to just cast the
// backend plugin itself to ICockpitPluginUi, which stopped being possible once the two parts physically split.
internal static class DiagramPluginUi
{
    public const string PluginId = "diagram";

    public static void Initialize(DiscoveredPlugin discovered, ICockpitPlugin plugin, ICockpitHost host, PluginChannelHub hub)
    {
        var ui = PluginUiManager.ActivateUi(discovered, plugin) ?? throw new InvalidOperationException("Diagram's UI part did not activate.");
        ui.InitializeUi(new CockpitUiHost(PluginId, host, new ServiceCollection().AddSingleton(hub).BuildServiceProvider()));
    }
}
