using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Browser.Contracts;

namespace Cockpit.Plugin.Browser;

internal sealed class BrowserSettings(IPluginStorage storage)
{
    // On by default; off leaves Playwright MCP to start its own browser as it always has.
    public bool AgentsUseCockpitBrowser => storage.Get<bool?>("agentsUseCockpitBrowser") ?? true;

    public IReadOnlyList<string> AgentNetworkAllowlist => storage.Get<string[]>("agentNetworkAllowlist") ?? [];

    public void SaveAgentNetworkAllowlist(string[] entries)
    {
        if (entries.Any(entry => !NetworkAllowlist.IsValid(entry)))
        {
            throw new ArgumentException("Use a host, host:port or CIDR for each agent network allowlist entry.", nameof(entries));
        }
        storage.Set("agentNetworkAllowlist", entries);
    }
}
