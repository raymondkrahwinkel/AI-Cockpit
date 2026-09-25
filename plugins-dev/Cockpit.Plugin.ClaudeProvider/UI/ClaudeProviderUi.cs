using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.ClaudeProvider.UI;

// The UI part of the Claude provider (AC-1393): registers the "add/edit profile" config view the backend part's
// SessionProviderRegistration only placeholds. ClaudeProviderConfigView reaches the host directly (not through
// the channel) for the managed-CLI/help-hint operations ICockpitUiHost already offers a UI part.
public sealed class ClaudeProviderUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddProviderConfigView(ClaudeProviderIds.Claude, existingConfigJson => new ClaudeProviderConfigView(existingConfigJson, host));
    }
}
