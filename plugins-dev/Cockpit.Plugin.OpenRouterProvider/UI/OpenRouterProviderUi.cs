using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.OpenRouterProvider.UI;

// The UI part of the OpenRouter provider (AC-1393): registers the "add/edit profile" config view the backend
// part's SessionProviderRegistration only placeholds.
public sealed class OpenRouterProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("openrouter-provider.openrouter", existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiCompatDefaultBaseUrls.OpenRouter, host));
    }
}
