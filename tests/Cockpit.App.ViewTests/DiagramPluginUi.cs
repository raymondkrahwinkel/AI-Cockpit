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

    public static void Initialize(DiscoveredPlugin discovered, ICockpitPlugin plugin, ICockpitHost host, PluginChannelHub hub, RecordingUiSurfaces surfaces)
    {
        var ui = PluginUiManager.ActivateUi(discovered, plugin) ?? throw new InvalidOperationException("Diagram's UI part did not activate.");
        var services = new ServiceCollection().AddSingleton(hub).AddSingleton<IWorkspaceTypeRegistry>(provider => new WorkspaceTypeRegistry(provider)).BuildServiceProvider();
        ui.InitializeUi(new CockpitUiHost(PluginId, "Diagram", host, services, surfaces, surfaces, surfaces, surfaces, []));
    }
}
