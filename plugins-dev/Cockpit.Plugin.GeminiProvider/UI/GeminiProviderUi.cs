using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GeminiProvider.UI;

// The UI part of the Gemini/OpenAI provider (AC-1393): registers the "add/edit profile" config view for both
// providers this plugin's backend part registers — each SessionProviderRegistration.CreateConfigView placeholds.
public sealed class GeminiProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("gemini-provider.gemini", existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiCompatDefaultBaseUrls.Gemini, host));
        host.AddProviderConfigView("gemini-provider.openai", existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiCompatDefaultBaseUrls.OpenAi, host));
    }
}
