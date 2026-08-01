using Cockpit.Core.Mcp;

namespace Cockpit.Core.Projects;

/// <summary>
/// A project's change to the MCP servers its sessions see (AC-159, "variant B"): the global registry stays the
/// base, and a project brings servers of its own, overrides one by name, and says which start ticked. Deliberately
/// a change rather than a list of its own — servers live in one registry, and a project that carried a full copy
/// would silently drift from it the moment a server is edited there.
/// <para>
/// A project narrows what is <em>selected</em>, never what is <em>offered</em> (Raymond, 2026-07-24): the New-session
/// checklist lists every server whichever project is picked, exactly as it does for a profile, and the project's
/// choice is the pre-selection — which beats the profile's when both have one. Removing a server from the list
/// instead would take a decision away from the operator that the checklist exists to give them.
/// </para>
/// <para>
/// <see cref="AdditionalServers"/> is complete in the model but no session mounts one yet, and nothing in the app
/// can produce one — see <c>IMcpServerCatalog.GetServersForProjectAsync</c> and AC-218.
/// </para>
/// </summary>
public sealed record ProjectMcpOverlay
{
    /// <summary>An overlay that changes nothing — what a project without MCP choices carries.</summary>
    public static ProjectMcpOverlay None { get; } = new();

    /// <summary>
    /// Names of servers this project's sessions start ticked, matched case-insensitively against
    /// <see cref="McpServerConfig.Name"/> — or <see langword="null"/> for a project that made no MCP choice at all,
    /// which starts every offered server ticked the way the registry intends.
    /// <para>
    /// Named the right way round on purpose (Raymond, 2026-08-01). It used to be <see cref="DisabledServerNames"/>
    /// alone, and a server added to the registry afterwards was in nobody's off-list — so it arrived ticked in every
    /// project, including the ones that had deliberately switched most servers off. A list of what is <em>on</em> has
    /// no such hole: a server nobody has decided about is simply not in it, and a project that narrowed its servers
    /// stays narrowed until the operator says otherwise.
    /// </para>
    /// <para>
    /// A pre-selection, not a removal (Raymond, 2026-07-24): the checklist still lists every server, exactly as it
    /// does for a profile — the project only decides what is ticked when it opens, and the operator can tick one back
    /// on for this session.
    /// </para>
    /// </summary>
    public IReadOnlyList<string>? EnabledServerNames { get; init; }

    /// <summary>
    /// The older shape of the same choice: names this project's sessions start <em>unticked</em>. Read for projects
    /// saved by an earlier build, which have no <see cref="EnabledServerNames"/> yet, and never written any more —
    /// the project editor replaces it with the list above the first time such a project is saved.
    /// </summary>
    public IReadOnlyList<string> DisabledServerNames { get; init; } = [];

    /// <summary>
    /// Servers this project brings itself. One whose name matches a registry server replaces it for this
    /// project's sessions — that is how a project overrides a global server rather than only adding to it.
    /// </summary>
    public IReadOnlyList<McpServerConfig> AdditionalServers { get; init; } = [];

    /// <summary>Whether this overlay would change anything, so a caller can skip the work for the common case of a project with no MCP choices.</summary>
    public bool IsEmpty => EnabledServerNames is null && DisabledServerNames.Count == 0 && AdditionalServers.Count == 0;

    /// <summary>
    /// Whether <paramref name="serverName"/> starts ticked under this project. The rule a project brings: its answer
    /// stands where it has one, and the profile's saved selection applies only to a session started without a project.
    /// A project that has chosen ticks exactly what it chose — a server it has never seen is not one of them — and a
    /// project that has chosen nothing ticks everything the checklist offers.
    /// </summary>
    public bool IsSelectedByDefault(string serverName) => EnabledServerNames is { } enabled
        ? enabled.Any(name => string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase))
        : !DisabledServerNames.Any(name => string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// <paramref name="servers"/> as this project's sessions see them: its own servers replacing same-named ones
    /// and appended otherwise. Nothing is removed — which servers start ticked is a pre-selection, applied where the
    /// checklist is built rather than here, so a project's servers are the registry's plus its own.
    /// </summary>
    public IReadOnlyList<McpServerConfig> ApplyTo(IReadOnlyList<McpServerConfig> servers)
    {
        // Only the project's own servers change this list; the pre-selection above does not, so a project that merely
        // narrowed what starts ticked takes the same shortcut a project with no overlay at all does.
        if (AdditionalServers.Count == 0)
        {
            return servers;
        }

        // First of a repeated name wins, the way the catalog's own merge resolves a collision — a hand-edited
        // config that lists a server twice should cost the operator the duplicate, not the whole load.
        var replacements = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in AdditionalServers)
        {
            replacements.TryAdd(server.Name, server);
        }

        // A project's own entry replaces the registry's by name — but never re-enables one the operator switched
        // off globally. Off in the registry means off everywhere; letting a project overrule that would put a
        // server the operator had retired back in front of them under its familiar name.
        var replaced = servers.Select(server =>
            replacements.TryGetValue(server.Name, out var replacement)
                ? replacement with { Enabled = replacement.Enabled && server.Enabled }
                : server);
        var known = servers.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = replacements.Values.Where(server => !known.Contains(server.Name));

        return [.. replaced.Concat(added)];
    }
}
