using ModelContextProtocol.Server;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>Minimal MCP tool hosted by the in-process test server standing in for "server-a" (#26 parallel-connect test).</summary>
internal sealed class McpTestToolA
{
    [McpServerTool(Name = "tool_a")]
    public string Ping() => "a";
}
