using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Matomo.UI;

// The UI part of Matomo (AC-2330): the settings view. The backend part, MatomoPlugin, keeps the MCP endpoint and
// answers Test connection over the plugin's channel.
public sealed class MatomoUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host) => host.AddSettings(() => new MatomoSettingsControl(host.Storage, host.Channel));

    // Fixed, network-free views for `--screenshot --scene plugin:<name>`.
    public static IReadOnlyDictionary<string, Func<Control>> Scenes => MatomoScenes.All;
}
