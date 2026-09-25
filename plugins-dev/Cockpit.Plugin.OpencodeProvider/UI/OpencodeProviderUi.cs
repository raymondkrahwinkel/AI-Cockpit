using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.OpencodeProvider.UI;

// The UI part of the opencode provider (AC-1393): registers the "add/edit profile" config view the backend
// part's SessionProviderRegistration only placeholds.
public sealed class OpencodeProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("opencode-provider.acp", existingConfigJson => new OpencodeProviderConfigView(existingConfigJson, host));
    }
}
