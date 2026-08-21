namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Whether the host mounts an <see cref="IPluginToolset"/> for a provider's sessions, and how much of it (AC-964).
/// Declared per provider on <see cref="PluginSessionCapabilities.HostToolLoop"/>.
/// </summary>
public enum PluginHostToolLoop
{
    /// <summary>
    /// No host toolset. The default, and what a provider that mounts <see cref="PluginMcpServer"/> endpoints
    /// itself (an agent CLI) or has no tools at all keeps.
    /// </summary>
    None = 0,

    /// <summary>
    /// The host connects, gates and runs the tools, and offers the model all of them on every turn.
    /// For a provider that brings a tool-search mechanism of its own, so the host's would be a second one.
    /// </summary>
    ToolsOnly = 1,

    /// <summary>
    /// As <see cref="ToolsOnly"/>, and above a host-chosen catalogue size the host swaps the bulk of the tools
    /// for its own search/call proxies. Only for a provider with no tool search of its own.
    /// </summary>
    ToolsAndSearch = 2,
}
