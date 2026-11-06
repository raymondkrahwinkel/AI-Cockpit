using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Assistant;

internal sealed class McpSetPolicy(IMcpServerCatalog? catalog, IMcpOAuthTokenStore? tokens)
{
    internal async Task<IReadOnlyList<string>> OutsideProjectAsync(string? projectId, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (catalog is null)
        {
            return [];
        }
        var scoped = await catalog.GetServersForProjectAsync(projectId, ct).ConfigureAwait(false);
        return [.. names.Where(name => !scoped.Any(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))];
    }

    internal async Task<string?> RefusalAsync(string? projectId, IReadOnlyList<string> names,
        bool add = true, IReadOnlySet<string>? delegationCeiling = null, CancellationToken cancellationToken = default)
    {
        if (catalog is null)
        {
            return "The MCP catalog is unavailable.";
        }
        var registry = await catalog.GetServersForSessionAsync(projectId, new ExactMcpServerSelection(names), cancellationToken).ConfigureAwait(false);
        foreach (var name in names)
        {
            var server = registry.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!add)
            {
                if (server?.AlwaysMounted == true)
                {
                    return $"MCP server '{name}' is always mounted and cannot be removed.";
                }
                continue;
            }
            var reason = server is null ? "unknown"
                : server.Internal ? "internal servers cannot be requested"
                : !server.Enabled ? "globally disabled"
                : delegationCeiling is not null && !delegationCeiling.Contains(server.Name) ? "outside the delegation profile ceiling"
                : null;
            if (reason is not null)
            {
                return $"MCP server '{name}' refused: {reason}.";
            }
            if (server is { Auth: McpServerAuth.OAuth } &&
                (tokens is null || await tokens.GetAsync(server.IdentityKey, cancellationToken).ConfigureAwait(false) is null))
            {
                return $"MCP server '{name}' needs OAuth authorization. Sign in before changing the set.";
            }
        }
        return null;
    }
}
