using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Workflows;

/// <summary>
/// The Workflows plugin's own settings, in its per-plugin storage. Today that is one thing: whether its MCP server
/// is offered to sessions (AC-40). The cockpit-workflows endpoint is cockpit-hosted and not listed in the MCP-servers
/// manager, so this is where it is turned on or off — read live by the endpoint's <c>isEnabled</c> gate and written
/// by the settings view.
/// </summary>
internal sealed class WorkflowsSettings(IPluginStorage storage)
{
    private const string McpEnabledKey = "mcp-enabled";

    /// <summary>Whether the cockpit-workflows MCP is offered to sessions. On by default until the operator turns it off.</summary>
    public bool McpEnabled => storage.Get<bool?>(McpEnabledKey) ?? true;

    public void SaveMcpEnabled(bool enabled) => storage.Set(McpEnabledKey, enabled);
}
