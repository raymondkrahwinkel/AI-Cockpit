using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Browser;

internal sealed class BrowserSettings(IPluginStorage storage)
{
    // On by default; off leaves Playwright MCP to start its own browser as it always has.
    public bool AgentsUseCockpitBrowser => storage.Get<bool?>("agentsUseCockpitBrowser") ?? true;
}
