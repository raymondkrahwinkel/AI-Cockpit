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
    /// <para>
    /// ⚠️ This is also the <em>only</em> overload the host's OAuth sign-in resolution falls back to
    /// (<c>CockpitHost.GetMcpServerAuthStateAsync</c>/<c>SignInMcpServerAsync</c>, AC-504) when a name is not in the
    /// shared registry — signing in happens from a plugin's own settings view, which has no project to scope a call
    /// to the overloads below by. A plugin whose servers are delivered to sessions per-project and that answers
    /// <see langword="null"/>/empty here unconditionally (rather than, say, every connection it knows about,
    /// unscoped) makes its own OAuth sign-in permanently unreachable — this is not merely the project-agnostic
    /// fallback for session delivery, it doubles as "everything this plugin could ever offer."
    /// </para>
    /// </summary>
    IReadOnlyList<McpServerContribution> GetMcpServers();

    /// <summary>
    /// The MCP servers this plugin provides for the session belonging to <paramref name="projectId"/> (AC-500), or
    /// <see langword="null"/> for a session with no project. Default forwards to the project-agnostic
    /// <see cref="GetMcpServers()"/>, so an existing implementation (test fakes, older plugin builds) keeps
    /// contributing the same global set to every session, unaware this overload exists. A plugin whose servers
    /// differ per project (a Depot connection scoped to one project's own space, say) overrides this instead, and
    /// still has to stay a cheap, synchronous read for the same reason <see cref="GetMcpServers()"/> does.
    /// </summary>
    IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId) => GetMcpServers();

    /// <summary>
    /// The MCP servers this plugin provides for the session belonging to <paramref name="projectId"/>, given
    /// <paramref name="projectMemorySchemes"/> (AC-504) — the scheme of every <c>Memory</c>-role row that project
    /// carries (e.g. <c>"depot.wispslate"</c> out of a stored reference <c>"depot.wispslate:my-slug"</c>), already
    /// parsed by the host so this stays plugin-ALC-safe (no <c>Cockpit.Core</c> type in the signature). Empty when
    /// the session has no project, or the project carries no Memory row. Default forwards to
    /// <see cref="GetMcpServers(string?)"/>, ignoring the schemes, so an existing implementation (test fakes, older
    /// plugin builds, a plugin whose servers do not depend on which memory connection a project points at) is
    /// unaffected.
    /// <para>
    /// A plugin that registered more than one <see cref="ICockpitHost.AddProjectMemorySource"/> scheme for itself
    /// (several Depot connections, each its own scheme) overrides this instead: <paramref name="projectId"/> alone
    /// cannot say <em>which</em> of those connections a given project actually uses, and the plugin is the only one
    /// that knows how its own schemes map back to its own servers.
    /// </para>
    /// </summary>
    IReadOnlyList<McpServerContribution> GetMcpServers(string? projectId, IReadOnlyList<string> projectMemorySchemes) =>
        GetMcpServers(projectId);
}
