using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App.ViewTests;

// AC-1400: the second half of loading Diagram as PluginManager does — InitializeUi after Initialize, on a UI host
// whose channel is the same hub the test host's backend Channel publishes on, so windows reach the registries.
internal static class DiagramPluginUi
{
    public const string PluginId = "diagram";

    public static void Initialize(ICockpitPlugin plugin, ICockpitHost host, PluginChannelHub hub) =>
        Assert.IsAssignableFrom<ICockpitPluginUi>(plugin)
            .InitializeUi(new CockpitUiHost(PluginId, host, new ServiceCollection().AddSingleton(hub).BuildServiceProvider()));
}
