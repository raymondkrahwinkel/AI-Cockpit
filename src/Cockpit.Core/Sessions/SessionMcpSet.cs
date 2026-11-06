namespace Cockpit.Core.Sessions;

public sealed record SessionMcpSet(SessionMcpContext? Context, IReadOnlySet<string> ServerNames)
{
    public bool PreserveSelection { get; init; }

    public IReadOnlySet<string>? DelegationCeiling { get; init; }

    public Cockpit.Core.Mcp.McpToolNames ToolNames { get; } = new();
}
