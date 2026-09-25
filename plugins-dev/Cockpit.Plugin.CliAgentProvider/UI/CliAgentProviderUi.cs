using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.CliAgentProvider.UI;

// The UI part of the CLI Agent (Codex) provider (AC-1393): registers the "add/edit profile" config view the
// backend part's SessionProviderRegistration only placeholds.
public sealed class CliAgentProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView("cli-agent-provider.codex", existingConfigJson => new CliAgentProviderConfigView(existingConfigJson, host));
    }
}
