using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.OnePassword.UI;

// The UI part of the 1Password plugin (AC-2348): its connect page under Options › PLUGINS.
public sealed class OnePasswordUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSettings(() => new OnePasswordConnectPage(host.Channel));
    }
}
