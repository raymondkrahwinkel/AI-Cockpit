using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Bitwarden.UI;

// The UI part of the Bitwarden plugin (AC-2349): its connect page under Options › PLUGINS.
public sealed class BitwardenUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSettings(() => new BitwardenConnectPage(host.Channel));
    }
}
