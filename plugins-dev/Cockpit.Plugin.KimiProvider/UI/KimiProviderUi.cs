using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.KimiProvider.UI;

// The UI part of the Kimi provider (AC-1393): registers the "add/edit profile" config view the backend part's
// SessionProviderRegistration only placeholds.
public sealed class KimiProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("kimi-provider.acp", existingConfigJson => new KimiProviderConfigView(existingConfigJson, host));
    }
}
