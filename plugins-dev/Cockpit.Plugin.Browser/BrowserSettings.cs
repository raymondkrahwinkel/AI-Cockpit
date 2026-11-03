using Cockpit.Plugins.Abstractions;
using Cockpit.Plugin.Browser.Contracts;

namespace Cockpit.Plugin.Browser;

internal sealed class BrowserSettings(IPluginStorage storage)
{
    private const string AgentNetworkAllowlistKey = "agentNetworkAllowlist";

    // On by default; off leaves Playwright MCP to start its own browser as it always has.
    public bool AgentsUseCockpitBrowser => storage.Get<bool?>("agentsUseCockpitBrowser") ?? true;

    public bool IsHardwareAccelerationEnabled
    {
        get => storage.Get<bool?>("hardwareAcceleration") ?? !OperatingSystem.IsLinux();
        set => storage.Set("hardwareAcceleration", value);
    }

    public IReadOnlyList<string> AgentNetworkAllowlist => storage.Get<string[]>(AgentNetworkAllowlistKey) ?? [];

    public void SaveAgentNetworkAllowlist(string[] entries)
    {
        if (entries.Any(entry => !NetworkAllowlist.IsValid(entry)))
        {
            throw new ArgumentException("Use a host, host:port or CIDR for each agent network allowlist entry.", nameof(entries));
        }
        storage.Set(AgentNetworkAllowlistKey, entries);
    }
}
