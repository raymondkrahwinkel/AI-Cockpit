using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GrokProvider.UI;

// The UI part of the Grok provider (AC-1393): registers the "add/edit profile" config view the backend part's
// SessionProviderRegistration only placeholds.
public sealed class GrokProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("grok-provider.grok", existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiCompatDefaultBaseUrls.Grok, host));
    }
}
