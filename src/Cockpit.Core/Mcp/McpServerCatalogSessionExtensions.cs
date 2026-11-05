using Cockpit.Core.Abstractions.Mcp;

namespace Cockpit.Core.Mcp;

public static class McpServerCatalogSessionExtensions
{
    // AC-2083: an explicit server list overrules project and profile, so a named server that the project's own
    // catalog doesn't carry (another project's, or the brain's on another Depot) is looked up in the unscoped one.
    public static async Task<IReadOnlyList<McpServerConfig>> GetServersForSessionAsync(
        this IMcpServerCatalog catalog, string? projectId, IReadOnlySet<string>? selection, CancellationToken cancellationToken = default)
    {
        var scoped = await catalog.GetServersForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (selection is not ExactMcpServerSelection exact || exact.All(name => scoped.Any(server => exact.Comparer.Equals(server.Name, name))))
        {
            return scoped;
        }

        var all = await catalog.GetServersAsync(cancellationToken).ConfigureAwait(false);
        return [.. scoped, .. all.Where(server => exact.Contains(server.Name) && !scoped.Any(known => exact.Comparer.Equals(known.Name, server.Name)))];
    }
}
