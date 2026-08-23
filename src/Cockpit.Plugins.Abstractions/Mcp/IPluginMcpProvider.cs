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
    /// <summary>
    /// The MCP servers this plugin currently provides, or an empty list when it has none configured.
    /// </summary>
    /// <remarks>
    /// ⚠️ Also the <em>only</em> overload the host's OAuth sign-in resolution falls back to (AC-504) when a name
    /// is not in the shared registry. A plugin whose servers are delivered per-project must not answer
    /// null/empty here unconditionally — this doubles as "everything this plugin could ever offer."
    /// </remarks>
    IReadOnlyList<McpServerContribution> GetMcpServers();

    /// <summary>
    /// The MCP servers this plugin provides for the session belonging to <paramref name="projectId"/> (AC-500),
    /// or <see langword="null"/> for a session with no project.
    /// </summary>
    /// <remarks>
    /// Default forwards to <see cref="GetMcpServers()"/>. A plugin whose servers differ per project overrides
    /// this instead, and must stay a cheap, synchronous read.
    /// </remarks>
    IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId) => GetMcpServers();

    /// <summary>
    /// The MCP servers this plugin provides for the session belonging to <paramref name="projectId"/>, given
    /// <paramref name="projectMemorySchemes"/> (AC-504) — the scheme of every <c>Memory</c>-role row that project
    /// carries.
    /// </summary>
    /// <remarks>
    /// Empty when the session has no project or it carries no Memory row. Default forwards to
    /// <see cref="GetMcpServers(string?)"/>, ignoring the schemes. Override this when a plugin registered more
    /// than one <see cref="ICockpitHost.AddProjectMemorySource"/> scheme for itself, since
    /// <paramref name="projectId"/> alone cannot say which connection a project actually uses.
    /// </remarks>
    IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes) =>
        GetMcpServers(projectId);
}
