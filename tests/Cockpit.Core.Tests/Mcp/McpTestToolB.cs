using ModelContextProtocol.Server;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>Minimal MCP tool hosted by the in-process test server standing in for "server-b" (#26 parallel-connect test).</summary>
internal sealed class McpTestToolB
{
    [McpServerTool(Name = "tool_b")]
    public string Ping() => "b";
}
