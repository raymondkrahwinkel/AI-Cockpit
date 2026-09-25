using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.ViewTests;

// The second half of loading Diagram as PluginManager/PluginUiManager do — InitializeUi on a UI host sharing the
// test host's channel hub. F2.13/AC-1401: activates the UI part via PluginUiManager.ActivateUi like production does,
// instead of casting the backend plugin to ICockpitPluginUi — no longer possible once the parts physically split.
internal static class DiagramPluginUi
{
    public const string PluginId = "diagram";

    public static void Initialize(DiscoveredPlugin discovered, ICockpitPlugin plugin, ICockpitHost host, PluginChannelHub hub)
    {
        var ui = PluginUiManager.ActivateUi(discovered, plugin) ?? throw new InvalidOperationException("Diagram's UI part did not activate.");
        ui.InitializeUi(new CockpitUiHost(PluginId, host, new ServiceCollection().AddSingleton(hub).BuildServiceProvider()));
    }
}
