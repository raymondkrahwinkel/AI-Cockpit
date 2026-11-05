namespace Cockpit.Core.Sessions;

public sealed record SessionMcpSet(SessionMcpContext? Context, IReadOnlySet<string> ServerNames)
{
    public Cockpit.Core.Mcp.McpToolNames ToolNames { get; } = new();
}
