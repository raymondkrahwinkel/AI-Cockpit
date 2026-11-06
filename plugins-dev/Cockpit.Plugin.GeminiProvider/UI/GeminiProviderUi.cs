using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GeminiProvider.UI;

// The UI part of the OpenAI-compatible provider (AC-1393): registers the "add/edit profile" config view for every
// provider this plugin's backend part registers — each SessionProviderRegistration.CreateConfigView placeholds.
public sealed class GeminiProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        foreach (var provider in OpenAiCompatProvider.All)
        {
            host.AddProviderConfigView(provider.ProviderId, existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, provider, host));
        }
    }
}
