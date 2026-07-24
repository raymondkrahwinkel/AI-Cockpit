using Cockpit.Core.Mcp;

namespace Cockpit.Core.Projects;

/// <summary>
/// A project's change to the MCP servers its sessions see (AC-159, "variant B"): the global registry stays the
/// base, and a project turns individual servers off, adds its own, or overrides one by name. Deliberately a
/// change rather than a list of its own — servers live in one registry, and a project that carried a full copy
/// would silently drift from it the moment a server is edited there.
/// </summary>
public sealed record ProjectMcpOverlay
{
    /// <summary>An overlay that changes nothing — what a project without MCP choices carries.</summary>
    public static ProjectMcpOverlay None { get; } = new();

    /// <summary>Names of servers this project's sessions do not get, matched case-insensitively against <see cref="McpServerConfig.Name"/>.</summary>
    public IReadOnlyList<string> DisabledServerNames { get; init; } = [];

    /// <summary>
    /// Servers this project brings itself. One whose name matches a registry server replaces it for this
    /// project's sessions — that is how a project overrides a global server rather than only adding to it.
    /// </summary>
    public IReadOnlyList<McpServerConfig> AdditionalServers { get; init; } = [];

    /// <summary>Whether this overlay would change anything, so a caller can skip the work for the common case of a project with no MCP choices.</summary>
    public bool IsEmpty => DisabledServerNames.Count == 0 && AdditionalServers.Count == 0;

    /// <summary>
    /// <paramref name="servers"/> as this project's sessions see them: its own servers replacing same-named ones
    /// and appended otherwise, minus everything <see cref="DisabledServerNames"/> names.
    /// <para>
    /// Disabling wins over adding, including over this project's own servers. That is what the manager's per-row
    /// toggle needs: switching off a project-owned server has to leave the server defined and merely off, or the
    /// only way to silence one would be to delete it and type it back in later.
    /// </para>
    /// </summary>
    public IReadOnlyList<McpServerConfig> ApplyTo(IReadOnlyList<McpServerConfig> servers)
    {
        if (IsEmpty)
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

        var replaced = servers.Select(server => replacements.GetValueOrDefault(server.Name, server));
        var known = servers.Select(server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = replacements.Values.Where(server => !known.Contains(server.Name));

        var disabled = DisabledServerNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. replaced.Concat(added).Where(server => !disabled.Contains(server.Name))];
    }
}
