using Cockpit.Core.Mcp;

namespace Cockpit.Core.Sessions;

public sealed record SessionMcpSet(SessionMcpContext? Context, IReadOnlySet<string> ServerNames)
{
    public bool PreserveSelection { get; init; }

    public IReadOnlySet<string>? DelegationCeiling { get; init; }

    internal (string PluginId, IReadOnlySet<string> Names)? PluginOwnedServers { get; init; }

    public bool AllowsServer(McpServerConfig server) =>
        PluginOwnedServers is not { } owned || !owned.Names.Contains(server.Name)
        || string.Equals(server.ContributingPluginId, owned.PluginId, StringComparison.Ordinal);

    public Cockpit.Core.Mcp.McpToolNames ToolNames { get; } = new();
}
