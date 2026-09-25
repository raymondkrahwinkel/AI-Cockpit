using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.GitHubModelsProvider.UI;

// The UI part of the GitHub Models provider (AC-1393): registers the "add/edit profile" config view the
// backend part's SessionProviderRegistration only placeholds.
public sealed class GitHubModelsProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("github-models-provider.github-models", existingConfigJson => new OpenAiCompatProviderConfigView(existingConfigJson, OpenAiCompatDefaultBaseUrls.GitHubModels, host));
    }
}
