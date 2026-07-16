namespace Cockpit.Plugins.Abstractions.Mcp;

/// <summary>
/// Implemented by a plugin that owns MCP servers of its own (#60, AC-11) — a YouTrack instance's remote endpoint,
/// say. The host asks each active plugin for these when it assembles a session's tool set, rather than the plugin
/// pushing them into the shared registry via <see cref="ICockpitHost.AddMcpServer"/>. That keeps the plugin the
/// sole owner of its MCP configuration: it answers with whatever it currently has, so a URL or token it changes
/// takes effect without touching — or having to keep in sync — any other store.
/// </summary>
/// <remarks>
/// Called on the UI thread each time a session's servers are gathered (session start, and the New-session
/// dialog's per-session checklist), so it must be a cheap, synchronous read of what the plugin already holds — not
/// a network call. What it returns is what the operator sees offered for a session and can untick there; the
/// servers never appear in the MCP-servers manager, which lists only the user-managed registry.
/// </remarks>
public interface IPluginMcpProvider
{
    /// <summary>The MCP servers this plugin currently provides, or an empty list when it has none configured.</summary>
    IReadOnlyList<McpServerContribution> GetMcpServers();
}
