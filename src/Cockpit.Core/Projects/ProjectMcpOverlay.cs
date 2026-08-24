using Cockpit.Core.Mcp;

namespace Cockpit.Core.Projects;

// AC-1013: A project's change to the MCP servers its sessions see (AC-159, "variant B") — a diff on the global
// registry, not a copy of it, so it never drifts. Narrows what is *selected* (a pre-selection), never what is
// *offered* (Raymond, 2026-07-24). AdditionalServers is modeled but not yet mounted by any session (AC-218).
public sealed record ProjectMcpOverlay
{
    // An overlay that changes nothing — what a project without MCP choices carries.
    public static ProjectMcpOverlay None { get; } = new();

    // AC-1013: Server names this project's sessions start ticked; null means no MCP choice, starting everything
    // ticked. Named the right way round (Raymond, 2026-08-01) — used to be DisabledServerNames alone, which let a
    // newly registered server arrive ticked even in projects that had deliberately switched most servers off.
    public IReadOnlyList<string>? EnabledServerNames { get; init; }

    // AC-1013: Older shape of the same choice (names started *unticked*), read for pre-AC-736 projects and
    // migrated to EnabledServerNames on first save. AC-766 repurposes this same field for a project-linked
    // server's own "off" (EnabledServerNames can't express that); an older build ignores this repurposing safely.
    public IReadOnlyList<string> DisabledServerNames { get; init; } = [];

    // Servers this project brings itself. One whose name matches a registry server replaces it for this
    // project's sessions — that is how a project overrides a global server rather than only adding to it.
    public IReadOnlyList<McpServerConfig> AdditionalServers { get; init; } = [];

    // Whether this overlay would change anything, so a caller can skip the work for the common case of a project with no MCP choices.
    public bool IsEmpty => EnabledServerNames is null && DisabledServerNames.Count == 0 && AdditionalServers.Count == 0;

    // AC-1013: Whether `serverName` starts ticked under this project — a project's answer stands where it has
    // one; the profile's selection applies only without a project.
    public bool IsSelectedByDefault(string serverName) => EnabledServerNames is { } enabled
        ? enabled.Any(name => string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase))
        : !DisabledServerNames.Any(name => string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase));

    // AC-1013: Same answer for a catalog-resolved project-linked server (AC-736) — starts ticked regardless of
    // EnabledServerNames since it never had an editor row; DisabledServerNames (repurposed, AC-766) is what
    // can turn it off instead.
    public bool IsSelectedByDefault(McpServerConfig server) => server.ProjectLinked
        ? !DisabledServerNames.Any(name => string.Equals(name, server.Name, StringComparison.OrdinalIgnoreCase))
        : IsSelectedByDefault(server.Name);

    // `servers` as this project's sessions see them: its own servers replacing same-named ones
    // and appended otherwise. Nothing is removed — which servers start ticked is a pre-selection, applied where the
    // checklist is built rather than here, so a project's servers are the registry's plus its own.
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
