namespace Cockpit.Core.Mcp;

// How the cockpit connects to a user-configured MCP server (#26 tools/MCP).
public enum McpTransport
{
    // A local process launched by the cockpit, spoken to over stdio (command + args).
    Stdio,

    // A remote server reached over HTTP (streamable HTTP / SSE) at a URL.
    Http,
}
