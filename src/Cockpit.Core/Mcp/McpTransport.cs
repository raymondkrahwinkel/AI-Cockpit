namespace Cockpit.Core.Mcp;

/// <summary>How the cockpit connects to a user-configured MCP server (#26 tools/MCP).</summary>
public enum McpTransport
{
    /// <summary>A local process launched by the cockpit, spoken to over stdio (command + args).</summary>
    Stdio,

    /// <summary>A remote server reached over HTTP (streamable HTTP / SSE) at a URL.</summary>
    Http,
}
