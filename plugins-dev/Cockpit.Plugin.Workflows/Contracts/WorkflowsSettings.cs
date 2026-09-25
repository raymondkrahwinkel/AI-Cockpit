using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows.Contracts;

// The Workflows plugin's own settings, in its per-plugin storage: whether its MCP server is offered to sessions
// (AC-40), and how far a scheduled trigger may catch up after the cockpit was not running to see it fire (#AC-1359).
// The cockpit-workflows endpoint is cockpit-hosted and not listed in the MCP-servers manager, so this is where it is
// turned on or off — read live by the endpoint's `isEnabled` gate and written by the settings view.
internal sealed class WorkflowsSettings(IPluginStorage storage)
{
    private const string McpEnabledKey = "mcp-enabled";
    private const string CatchUpGraceMinutesKey = "catch-up-grace-minutes";

    // The default grace: long enough to cover an overnight restart for maintenance, short enough that a flow which
    // slept through most of a day still says so rather than quietly running hours late.
    internal const int DefaultCatchUpGraceMinutes = 60;

    // Whether the cockpit-workflows MCP is offered to sessions. On by default until the operator turns it off.
    public bool McpEnabled => storage.Get<bool?>(McpEnabledKey) ?? true;

    public void SaveMcpEnabled(bool enabled) => storage.Set(McpEnabledKey, enabled);

    // How long, after the cockpit was not running to see a scheduled slot fire, it is still worth running late
    // rather than logging as missed. One setting for every flow — not per flow, because the honest answer to "was
    // this cockpit down too long" does not change with which flow you ask.
    public int CatchUpGraceMinutes => storage.Get<int?>(CatchUpGraceMinutesKey) ?? DefaultCatchUpGraceMinutes;

    public void SaveCatchUpGraceMinutes(int minutes) => storage.Set(CatchUpGraceMinutesKey, Math.Max(0, minutes));
}
